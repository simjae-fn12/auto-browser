using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace PluckWindowsNative;

// Agent input goes through the DevTools Input domain so pages receive trusted events (event.isTrusted),
// the same as a person's mouse and keyboard. Script-made events are only a fallback.
public sealed partial class BrowserForm
{
    // Finds a target in a frame: a snapshot ref (@3), "text=Visible words", or a CSS selector.
    private const string LocateScript = """
        const __pluckLocate = (target) => {
          if (target.startsWith('@')) return document.querySelector('[data-pluck-native-ref="' + CSS.escape(target.slice(1)) + '"]');
          if (target.startsWith('text=')) {
            const wanted = target.slice(5).trim().toLowerCase();
            const candidates = [...document.querySelectorAll('a,button,input,textarea,select,summary,label,[role],[onclick],[tabindex],h1,h2,h3,h4,li,td,span,div')];
            const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
            const name = el => (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '').trim().toLowerCase();
            // The innermost visible element whose own text matches wins, so "text=Save" picks the button, not the page.
            return candidates.filter(el => visible(el) && name(el) === wanted).at(-1)
              || candidates.filter(el => visible(el) && name(el).includes(wanted)).at(-1) || null;
          }
          return document.querySelector(target);
        };
        """;

    private static string FrameIdOf(string target) =>
        System.Text.RegularExpressions.Regex.Match(target, "^@(?<frame>f[0-9]+):").Groups["frame"] is { Success: true } g ? g.Value : "f0";

    private static string SelectorOf(string target) =>
        System.Text.RegularExpressions.Regex.Match(target, "^@(?<frame>f[0-9]+):(?<ref>[0-9]+)$") is { Success: true } m ? "@" + m.Groups["ref"].Value : target;

    private static async Task<string> RunInFrameAsync(BrowserTab tab, string frameId, string script)
    {
        if (frameId != "f0")
            return await (tab.Frames.GetValueOrDefault(frameId) ?? throw new InvalidOperationException("Frame changed. Take a new snapshot.")).ExecuteScriptAsync(script);
        // ExecuteScriptAsync turns a thrown error into null; this keeps the page's message, such as "Element not found".
        var result = await tab.View.CoreWebView2!.ExecuteScriptWithResultAsync(script);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Exception?.Message is { Length: > 0 } message
                ? message.Split('\n')[0].Replace("Error: ", "").Trim() : "Script failed. Take a new snapshot.");
        return result.ResultAsJson;
    }

    // DevTools rejects explicit nulls ("Value does not fall within the expected range"), so omit them.
    private static readonly JsonSerializerOptions CdpJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static Task<string> CdpAsync(CoreWebView2 core, string method, object args) =>
        core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args, CdpJson));

    private static async Task MouseClickAsync(CoreWebView2 core, double x, double y, int clickCount = 1)
    {
        await CdpAsync(core, "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none" });
        await CdpAsync(core, "Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount });
        await CdpAsync(core, "Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount });
    }

    // Scrolls the target into view and returns its centre in top-level viewport pixels. The frame offset is only
    // knowable for same-origin frames; elsewhere offsetKnown is false and the caller falls back to a script click.
    private static async Task<JsonNode?> PrepareTargetAsync(BrowserTab tab, string frameId, string selector, bool probeClick)
    {
        var script = $$"""
            (() => {
              {{LocateScript}}
              const el = __pluckLocate({{JsonSerializer.Serialize(selector)}});
              if (!el) throw new Error('Element not found. Take a new snapshot.');
              el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
              const r = el.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              const covered = !hit || !(hit === el || el.contains(hit) || hit.contains(el));
              let ox = 0, oy = 0, offsetKnown = true;
              try {
                for (let w = window; w !== w.top; w = w.parent) {
                  const f = w.frameElement; if (!f) { offsetKnown = false; break; }
                  const fr = f.getBoundingClientRect(); ox += fr.left + f.clientLeft; oy += fr.top + f.clientTop;
                }
              } catch { offsetKnown = false; }
              if ({{(probeClick ? "true" : "false")}}) {
                window.__pluckTrusted = false;
                // Only a trusted click that lands on the target counts; one that hits something else falls back.
                const listener = e => { if (e.isTrusted && (e.target === el || el.contains(e.target))) window.__pluckTrusted = true; };
                document.addEventListener('click', listener, { capture: true, once: true });
              }
              return { x: x + ox, y: y + oy, covered, offsetKnown, w: r.width, h: r.height };
            })()
            """;
        return JsonNode.Parse(await RunInFrameAsync(tab, frameId, script));
    }

    private async Task<string> ClickAsync(BrowserTab tab, string target)
    {
        var frameId = FrameIdOf(target);
        var selector = SelectorOf(target);
        var core = tab.View.CoreWebView2!;
        var box = await PrepareTargetAsync(tab, frameId, selector, probeClick: true)
            ?? throw new InvalidOperationException("Action failed or frame changed. Take a new snapshot.");
        if (box["offsetKnown"]?.GetValue<bool>() == true && box["covered"]?.GetValue<bool>() != true)
        {
            // Moving the pointer can close hover menus and shift the layout, so measure again once it has arrived.
            await CdpAsync(core, "Input.dispatchMouseEvent", new { type = "mouseMoved", x = box["x"]!.GetValue<double>(), y = box["y"]!.GetValue<double>(), button = "none" });
            box = await PrepareTargetAsync(tab, frameId, selector, probeClick: true) ?? box;
            await MouseClickAsync(core, box["x"]!.GetValue<double>(), box["y"]!.GetValue<double>());
            // A navigation replaces the document, so a missing probe means the click already took effect.
            string probe;
            try { probe = await RunInFrameAsync(tab, frameId, "window.__pluckTrusted"); }
            catch (InvalidOperationException) { return "trusted"; }
            if (probe != "false") return "trusted";
        }
        var fallback = $$"""
            (() => {
              {{LocateScript}} const el = __pluckLocate({{JsonSerializer.Serialize(selector)}}); if (!el) return false; el.focus?.(); el.click(); return true; })()
            """;
        if (await RunInFrameAsync(tab, frameId, fallback) != "true") throw new InvalidOperationException("Element not found. Take a new snapshot.");
        return "script";
    }

    private async Task<string> FillAsync(BrowserTab tab, string target, string value)
    {
        var frameId = FrameIdOf(target);
        var selector = SelectorOf(target);
        var core = tab.View.CoreWebView2!;
        var focus = $$"""
            (() => {
              {{LocateScript}}
              const el = __pluckLocate({{JsonSerializer.Serialize(selector)}});
              if (!el) throw new Error('Element not found. Take a new snapshot.');
              el.scrollIntoView({ block: 'center', behavior: 'instant' });
              el.focus();
              if ('value' in el && typeof el.select === 'function') { el.select(); return 'value'; }
              if (el.isContentEditable) {
                const range = document.createRange(); range.selectNodeContents(el);
                const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
                return 'editable';
              }
              throw new Error('Element cannot be filled');
            })()
            """;
        var kind = JsonNode.Parse(await RunInFrameAsync(tab, frameId, focus))?.GetValue<string>();
        // insertText replaces the selection exactly as typing would, firing beforeinput/input for editors like React and ProseMirror.
        if (value.Length > 0) await CdpAsync(core, "Input.insertText", new { text = value });
        else await PressKeyAsync(core, "Delete");
        var check = $$"""
            (() => {
              {{LocateScript}}
              const el = __pluckLocate({{JsonSerializer.Serialize(selector)}}); if (!el) return false;
              const wanted = {{JsonSerializer.Serialize(value)}};
              return {{(kind == "value" ? "el.value === wanted" : "(el.innerText || '').replace(/\\s+/g, ' ').trim() === wanted.replace(/\\s+/g, ' ').trim()")}};
            })()
            """;
        if (await RunInFrameAsync(tab, frameId, check) == "true") return "trusted";
        if (kind != "value") return "trusted-unverified";
        var fallback = $$"""
            (() => {
              {{LocateScript}}
              const el = __pluckLocate({{JsonSerializer.Serialize(selector)}}); if (!el) return false;
              const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
              if (setter) setter.call(el, {{JsonSerializer.Serialize(value)}}); else el.value = {{JsonSerializer.Serialize(value)}};
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """;
        await RunInFrameAsync(tab, frameId, fallback);
        return "script";
    }

    private async Task HoverAsync(BrowserTab tab, string target)
    {
        var box = await PrepareTargetAsync(tab, FrameIdOf(target), SelectorOf(target), probeClick: false)
            ?? throw new InvalidOperationException("Action failed or frame changed. Take a new snapshot.");
        if (box["offsetKnown"]?.GetValue<bool>() != true) throw new InvalidOperationException("Cannot hover inside a cross-origin frame");
        await CdpAsync(tab.View.CoreWebView2!, "Input.dispatchMouseEvent",
            new { type = "mouseMoved", x = box["x"]!.GetValue<double>(), y = box["y"]!.GetValue<double>(), button = "none" });
    }

    private static async Task<string> SelectOptionAsync(BrowserTab tab, string target, string value)
    {
        var script = $$"""
            (() => {
              {{LocateScript}}
              const el = __pluckLocate({{JsonSerializer.Serialize(SelectorOf(target))}});
              if (!el || el.tagName !== 'SELECT') throw new Error('Target is not a <select>. Click custom dropdowns instead.');
              const wanted = {{JsonSerializer.Serialize(value)}};
              const option = [...el.options].find(o => o.value === wanted) || [...el.options].find(o => o.text.trim() === wanted)
                || [...el.options].find(o => o.text.trim().toLowerCase().includes(wanted.toLowerCase()));
              if (!option) throw new Error('No option matches: ' + wanted + '. Options: ' + [...el.options].map(o => o.text.trim()).join(' | '));
              el.focus(); el.value = option.value;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              return option.text.trim();
            })()
            """;
        return JsonNode.Parse(await RunInFrameAsync(tab, FrameIdOf(target), script))?.GetValue<string>() ?? "";
    }

    private static readonly Dictionary<string, (int Code, string KeyCode, string? Text)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = (13, "Enter", "\r"), ["Tab"] = (9, "Tab", null), ["Escape"] = (27, "Escape", null),
        ["Backspace"] = (8, "Backspace", null), ["Delete"] = (46, "Delete", null), ["Space"] = (32, "Space", " "),
        ["ArrowUp"] = (38, "ArrowUp", null), ["ArrowDown"] = (40, "ArrowDown", null),
        ["ArrowLeft"] = (37, "ArrowLeft", null), ["ArrowRight"] = (39, "ArrowRight", null),
        ["Home"] = (36, "Home", null), ["End"] = (35, "End", null), ["PageUp"] = (33, "PageUp", null), ["PageDown"] = (34, "PageDown", null),
        ["Insert"] = (45, "Insert", null),
    };

    // Accepts "Enter", "a", "Control+A", "Shift+Tab", "Control+Shift+ArrowLeft", F1–F12.
    private static async Task PressKeyAsync(CoreWebView2 core, string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new InvalidOperationException("Key required");
        var modifiers = 0;
        foreach (var modifier in parts[..^1])
            modifiers |= modifier.ToLowerInvariant() switch
            {
                "alt" => 1, "control" or "ctrl" => 2, "meta" or "win" => 4, "shift" => 8,
                _ => throw new InvalidOperationException($"Unknown modifier: {modifier}")
            };
        var key = parts[^1] == "Space" ? " " : parts[^1];
        int vk; string code; string? text;
        if (NamedKeys.TryGetValue(parts[^1], out var named)) { (vk, code, text) = named; key = parts[^1] == "Space" ? " " : code; }
        else if (key.Length == 1)
        {
            var c = key[0];
            vk = char.ToUpperInvariant(c) is >= 'A' and <= 'Z' or >= '0' and <= '9' ? char.ToUpperInvariant(c) : 0;
            code = char.IsLetter(c) && c < 128 ? "Key" + char.ToUpperInvariant(c) : char.IsDigit(c) ? "Digit" + c : "";
            text = (modifiers & ~8) == 0 ? ((modifiers & 8) != 0 ? key.ToUpperInvariant() : key) : null;
            // Shortcuts name the produced character, so Control+A reports key 'a' like a real keyboard.
            key = (modifiers & 8) != 0 && char.IsLetter(c) ? key.ToUpperInvariant() : char.IsLetter(c) ? key.ToLowerInvariant() : key;
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(key, "^F([1-9]|1[0-2])$")) { vk = 111 + int.Parse(key[1..]); code = key; text = null; }
        else throw new InvalidOperationException($"Unsupported key: {key}");
        // Ctrl/Alt shortcuts carry no text, so Chromium treats them as commands such as select-all.
        if ((modifiers & 3) != 0) text = null;
        await CdpAsync(core, "Input.dispatchKeyEvent", new
        {
            type = text == null ? "rawKeyDown" : "keyDown", modifiers, key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk,
            text, unmodifiedText = text
        });
        await CdpAsync(core, "Input.dispatchKeyEvent", new { type = "keyUp", modifiers, key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk });
    }

    private static async Task<bool> WheelAsync(CoreWebView2 core, string direction, string pixelsText)
    {
        if (direction is not ("up" or "down")) throw new InvalidOperationException("Direction must be up or down");
        var pixels = int.TryParse(pixelsText, out var parsed) ? Math.Clamp(parsed, 1, 10000) : 600;
        var view = JsonNode.Parse(await core.ExecuteScriptAsync("({ w: innerWidth, h: innerHeight, y: scrollY })"));
        var x = (view?["w"]?.GetValue<double>() ?? 800) / 2;
        var y = (view?["h"]?.GetValue<double>() ?? 600) / 2;
        var before = view?["y"]?.GetValue<double>() ?? 0;
        // A wheel scrolls whatever is under the pointer, including inner scroll panes that window.scrollBy misses.
        await CdpAsync(core, "Input.dispatchMouseEvent", new { type = "mouseWheel", x, y, deltaX = 0, deltaY = direction == "down" ? pixels : -pixels });
        await Task.Delay(120);
        var after = await core.ExecuteScriptAsync("scrollY");
        if (double.TryParse(after, System.Globalization.CultureInfo.InvariantCulture, out var moved) && moved != before) return true;
        // Some pages swallow wheel events; still move the document so the agent is not stuck.
        await core.ExecuteScriptAsync($"window.scrollBy(0, {(direction == "down" ? pixels : -pixels)})");
        return true;
    }

    // Draws each snapshot ref as a numbered badge, captures, then removes the badges.
    private const string AnnotateScript = """
        (() => {
          document.getElementById('__pluck_marks')?.remove();
          const layer = document.createElement('div'); layer.id = '__pluck_marks';
          layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483646';
          for (const el of document.querySelectorAll('[data-pluck-native-ref]')) {
            const r = el.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0 || r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue;
            const box = document.createElement('div');
            box.style.cssText = `position:fixed;left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px;outline:2px solid #e5484d;border-radius:3px`;
            const tag = document.createElement('span'); tag.textContent = el.getAttribute('data-pluck-native-ref');
            tag.style.cssText = 'position:absolute;left:-2px;top:-16px;background:#e5484d;color:#fff;font:600 11px/14px system-ui;padding:0 4px;border-radius:3px';
            box.append(tag); layer.append(box);
          }
          document.documentElement.append(layer);
          return layer.childElementCount;
        })()
        """;

    private static async Task<string> CaptureAsync(CoreWebView2 core, bool annotate)
    {
        if (annotate) await core.ExecuteScriptAsync(AnnotateScript);
        try
        {
            // DevTools capture works for tabs that are not on screen, but a hidden tab may not paint a new frame for a long
            // time. If the plain capture stalls, capturing beyond the viewport forces a fresh layout and paint.
            var plain = core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", """{"format":"png"}""");
            string json;
            if (await Task.WhenAny(plain, Task.Delay(2500)) == plain) json = await plain;
            else
            {
                var view = JsonNode.Parse(await core.ExecuteScriptAsync("({ w: innerWidth, h: innerHeight, x: scrollX, y: scrollY })"));
                var forced = core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(new
                {
                    format = "png", captureBeyondViewport = true,
                    clip = new { x = view?["x"]?.GetValue<double>() ?? 0, y = view?["y"]?.GetValue<double>() ?? 0,
                        width = view?["w"]?.GetValue<double>() ?? 800, height = view?["h"]?.GetValue<double>() ?? 600, scale = 1 }
                }));
                var first = await Task.WhenAny(plain, forced, Task.Delay(15000));
                if (first != plain && first != forced) throw new TimeoutException("Screenshot timed out: the tab is not painting. Call show(id) and retry.");
                json = await (Task<string>)first;
            }
            return JsonNode.Parse(json)?["data"]?.GetValue<string>() ?? throw new InvalidOperationException("Screenshot failed");
        }
        finally
        {
            if (annotate) await core.ExecuteScriptAsync("document.getElementById('__pluck_marks')?.remove()");
        }
    }
}
