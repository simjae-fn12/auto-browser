using System.Text.Json;
using System.Text.Json.Nodes;

namespace OinziBrowser;

// "Pick an element": the person clicks something on the page and the AI panel attaches what it is, so instructions
// like "이 버튼 말고 저거" can point at the exact element. Runs on the tab the person is viewing.
public sealed partial class BrowserForm
{
    private const string PickerScript = """
        new Promise(resolve => {
          const box = document.createElement('div'), tag = document.createElement('div');
          box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;border:2px solid #7462ee;background:rgba(116,98,238,.12);border-radius:4px;transition:all 60ms';
          tag.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;padding:3px 8px;border-radius:6px;background:#7462ee;color:#fff;font:600 12px "Segoe UI","Malgun Gothic",sans-serif;white-space:nowrap';
          const hint = document.createElement('div');
          hint.textContent = 'AI에게 알려 줄 요소를 클릭하세요 · Esc 취소';
          hint.style.cssText = 'position:fixed;z-index:2147483647;left:50%;top:14px;transform:translateX(-50%);padding:8px 14px;border-radius:10px;background:rgba(24,24,28,.88);color:#fff;font:600 12.5px "Segoe UI","Malgun Gothic",sans-serif;pointer-events:none';
          document.documentElement.append(box, tag, hint);
          let current = null;
          const label = el => (el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('alt') || el.getAttribute('placeholder') || el.getAttribute('title') || '').trim().replace(/\s+/g, ' ');
          const path = el => {
            if (el.id && document.querySelectorAll('#' + CSS.escape(el.id)).length === 1) return '#' + CSS.escape(el.id);
            const parts = [];
            for (let node = el; node && node.nodeType === 1 && node !== document.body && parts.length < 6; node = node.parentElement) {
              const test = node.getAttribute('data-testid'), name = node.getAttribute('name');
              if (test) { parts.unshift(`[data-testid="${CSS.escape(test)}"]`); break; }
              let part = node.localName + (name ? `[name="${CSS.escape(name)}"]` : '');
              const same = [...(node.parentElement?.children ?? [])].filter(other => other.localName === node.localName);
              if (!name && same.length > 1) part += `:nth-of-type(${same.indexOf(node) + 1})`;
              parts.unshift(part);
              if (node.id && document.querySelectorAll('#' + CSS.escape(node.id)).length === 1) { parts[0] = '#' + CSS.escape(node.id); break; }
            }
            return parts.join(' > ');
          };
          const move = event => {
            const el = document.elementFromPoint(event.clientX, event.clientY);
            if (!el || el === current) return;
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { left: r.left - 2 + 'px', top: r.top - 2 + 'px', width: r.width + 4 + 'px', height: r.height + 4 + 'px' });
            tag.textContent = el.localName + (label(el) ? ' · ' + label(el).slice(0, 40) : '');
            Object.assign(tag.style, { left: Math.max(4, r.left) + 'px', top: Math.max(4, r.top - 26) + 'px' });
          };
          const stop = event => { event.preventDefault(); event.stopImmediatePropagation(); };
          const finish = result => {
            removeEventListener('pointermove', move, true);
            for (const type of ['pointerdown', 'mousedown', 'mouseup', 'click']) removeEventListener(type, block, true);
            removeEventListener('keydown', key, true);
            box.remove(); tag.remove(); hint.remove();
            resolve(result);
          };
          const block = event => {
            stop(event);
            if (event.type !== 'click') return;
            const el = document.elementFromPoint(event.clientX, event.clientY);
            if (!el) return finish(null);
            const r = el.getBoundingClientRect();
            finish({ tag: el.localName, role: el.getAttribute('role') || '', text: label(el).slice(0, 160), selector: path(el),
              href: el.closest('a')?.href || '', rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)] });
          };
          const key = event => { if (event.key === 'Escape') { stop(event); finish(null); } };
          addEventListener('pointermove', move, true);
          for (const type of ['pointerdown', 'mousedown', 'mouseup', 'click']) addEventListener(type, block, true);
          addEventListener('keydown', key, true);
          setTimeout(() => finish(null), 120000);
        })
        """;

    private bool picking;

    private async Task PickElementAsync(string engine)
    {
        if (picking || Active() is not { View.CoreWebView2: { } core } tab) return;
        picking = true;
        try
        {
            ShowTab(tab);
            var reply = JsonNode.Parse(await CdpAsync(core, "Runtime.evaluate", new { expression = PickerScript, awaitPromise = true, returnByValue = true }));
            var picked = reply?["result"]?["value"];
            if (picked is not JsonObject element) { PostChrome(new { type = "ai.picked", engine }); return; }
            element["tab"] = tab.Id;
            element["url"] = tab.View.Source?.ToString() ?? "";
            PostChrome(new { type = "ai.picked", engine, element });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { PostChrome(new { type = "ai.picked", engine }); }
        finally { picking = false; }
    }

    // The picked element goes in front of the message, like the current-tab line.
    private static string PickedContext(string json)
    {
        if (json.Length == 0) return "";
        try
        {
            var el = JsonNode.Parse(json);
            if (el?["selector"]?.ToString() is not { Length: > 0 } selector) return "";
            return $"[Oinzi] 사용자가 페이지에서 고른 요소: <{el["tag"]}> \"{el["text"]}\" — CSS `{selector}`"
                + (el["href"]?.ToString() is { Length: > 0 } href ? $", 링크 {href}" : "")
                + $" (tab id {el["tab"]}, {el["url"]}). 이 탭은 activeTab()으로 다루고, 이 선택자나 새 스냅샷의 ref로 지정하세요.\n\n";
        }
        catch (JsonException) { return ""; }
    }
}
