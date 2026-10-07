using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EgoWindowsNative;

public sealed class BrowserForm : Form
{
    private sealed class BrowserTab(int id, string space, TabPage page, WebView2 view)
    {
        public int Id { get; } = id;
        public string Space { get; } = space;
        public bool Agent => Space != "user";
        public TabPage Page { get; } = page;
        public WebView2 View { get; } = view;
        public Dictionary<string, CoreWebView2Frame> Frames { get; } = [];
    }

    private sealed record SavedTab(string Url, string Space);

    private const string Home = "https://www.google.com/";
    private readonly string dataDir = Environment.GetEnvironmentVariable("EGO_NATIVE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ego-windows-native");
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox address = new() { Dock = DockStyle.Fill, PlaceholderText = "주소 또는 검색" };
    private readonly Dictionary<int, BrowserTab> browserTabs = [];
    private readonly HashSet<string> spaces = new(StringComparer.Ordinal);
    private CoreWebView2Environment? environment;
    private HttpListener? listener;
    private string? connectionFile;
    private int nextId = 1;

    public BrowserForm()
    {
        Text = "Ego Windows Native";
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(600, 400);
        KeyPreview = true;

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 5 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        AddButton(bar, "←", 0, () => { if (Active()?.View.CanGoBack == true) Active()!.View.GoBack(); });
        AddButton(bar, "→", 1, () => { if (Active()?.View.CanGoForward == true) Active()!.View.GoForward(); });
        AddButton(bar, "↻", 2, () => Active()?.View.Reload());
        bar.Controls.Add(address, 3, 0);
        AddButton(bar, "+", 4, () => _ = CreateTabAsync(Home, "user", true));
        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var tab = Active();
            if (tab != null) tab.View.CoreWebView2?.Navigate(NormalizeUrl(address.Text));
            tabs.Focus();
        };
        tabs.SelectedIndexChanged += (_, _) => RefreshAddress();
        Controls.Add(tabs);
        Controls.Add(bar);
        KeyDown += OnShortcut;
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => SaveTabs();
        FormClosed += (_, _) =>
        {
            listener?.Close();
            if (connectionFile != null) File.Delete(connectionFile);
        };
    }

    private static void AddButton(TableLayoutPanel bar, string label, int column, Action action)
    {
        var button = new Button { Text = label, Dock = DockStyle.Fill, TabStop = false };
        button.Click += (_, _) => action();
        bar.Controls.Add(button, column, 0);
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataDir, "profile"));
            var saved = ReadSavedTabs();
            var userTabs = saved.Where(tab => tab.Space == "user").ToArray();
            foreach (var tab in userTabs.Length == 0 ? [new SavedTab(Home, "user")] : userTabs)
                await CreateTabAsync(tab.Url, "user", true);
            foreach (var tab in saved.Where(tab => tab.Space != "user"))
            {
                spaces.Add(tab.Space);
                await CreateTabAsync(tab.Url, tab.Space, false);
            }
            StartServer();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dataDir, "startup-error.log"), ex.ToString());
            MessageBox.Show(this, ex.Message, "Ego Windows Native 시작 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private SavedTab[] ReadSavedTabs()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "tabs.json")));
            var items = root is JsonArray legacy
                ? legacy.Select(url => new SavedTab(url?.GetValue<string>() ?? "", "user"))
                : (root?["tabs"]?.AsArray() ?? []).Select(tab => new SavedTab(
                    tab?["url"]?.GetValue<string>() ?? "", tab?["space"]?.GetValue<string>() ?? "user"));
            return items.Where(tab => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" && tab.Space.Length is > 0 and <= 80)
                .Take(50).ToArray();
        }
        catch { return []; }
    }

    private void SaveTabs()
    {
        if (!Directory.Exists(dataDir)) return;
        var saved = browserTabs.Values.Select(tab => new { url = tab.View.Source?.ToString(), space = tab.Space })
            .Where(tab => tab.url?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        File.WriteAllText(Path.Combine(dataDir, "tabs.json"), JsonSerializer.Serialize(new { tabs = saved }));
    }

    private async Task<BrowserTab> CreateTabAsync(string? url, string space, bool select, bool navigate = true)
    {
        if (environment == null) throw new InvalidOperationException("Browser is not ready");
        var id = nextId++;
        var page = new TabPage(space == "user" ? "새 탭" : $"◆ {space} {id}");
        var view = new WebView2 { Dock = DockStyle.Fill };
        page.Controls.Add(view);
        var tab = new BrowserTab(id, space, page, view);
        browserTabs.Add(id, tab);
        tabs.TabPages.Add(page);
        if (select) tabs.SelectedTab = page;
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateTitle(tab);
            view.CoreWebView2.SourceChanged += (_, _) => { if (Active() == tab) RefreshAddress(); };
            view.CoreWebView2.NavigationStarting += (_, _) => tab.Frames.Clear();
            view.CoreWebView2.FrameCreated += (_, e) =>
            {
                var frameId = $"f{e.Frame.FrameId}";
                tab.Frames[frameId] = e.Frame;
                e.Frame.Destroyed += (_, _) => tab.Frames.Remove(frameId);
            };
            view.CoreWebView2.NewWindowRequested += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    var popup = await CreateTabAsync(null, tab.Space, !tab.Agent, navigate: false);
                    e.NewWindow = popup.View.CoreWebView2;
                }
                catch
                {
                    // Let WebView2 open its fallback popup if a tab cannot be created.
                    e.Handled = false;
                }
                finally { deferral.Complete(); }
            };
            view.CoreWebView2.WindowCloseRequested += (_, _) => CloseTab(tab);
            if (navigate) view.CoreWebView2.Navigate(NormalizeUrl(url));
            return tab;
        }
        catch
        {
            browserTabs.Remove(id);
            tabs.TabPages.Remove(page);
            view.Dispose();
            page.Dispose();
            throw;
        }
    }

    private void UpdateTitle(BrowserTab tab)
    {
        var title = tab.View.CoreWebView2?.DocumentTitle;
        tab.Page.Text = (tab.Agent ? $"◆ {tab.Space} · " : "") + (string.IsNullOrWhiteSpace(title) ? "새 탭" : title.Length > 24 ? title[..24] + "…" : title);
        if (Active() == tab) Text = $"{title} — Ego Windows Native";
    }

    private BrowserTab? Active() => browserTabs.Values.FirstOrDefault(tab => tab.Page == tabs.SelectedTab);

    private void RefreshAddress()
    {
        var tab = Active();
        address.Text = tab?.View.Source?.ToString() ?? "";
        Text = tab == null ? "Ego Windows Native" : $"{tab.View.CoreWebView2?.DocumentTitle} — Ego Windows Native";
    }

    private static string NormalizeUrl(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0) return Home;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return value;
        if (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || value.StartsWith("127.0.0.1")) return "http://" + value;
        if (!value.Contains(' ') && value.Contains('.')) return "https://" + value;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.L) { address.Focus(); address.SelectAll(); e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.T) { _ = CreateTabAsync(Home, "user", true); e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.W) { if (Active() is { } tab) CloseTab(tab); e.SuppressKeyPress = true; }
        else if (e.Alt && e.KeyCode == Keys.Left) { if (Active()?.View.CanGoBack == true) Active()!.View.GoBack(); e.SuppressKeyPress = true; }
        else if (e.Alt && e.KeyCode == Keys.Right) { if (Active()?.View.CanGoForward == true) Active()!.View.GoForward(); e.SuppressKeyPress = true; }
    }

    private void CloseTab(BrowserTab tab)
    {
        browserTabs.Remove(tab.Id);
        tabs.TabPages.Remove(tab.Page);
        tab.View.Dispose();
        tab.Page.Dispose();
        if (browserTabs.Count == 0) _ = CreateTabAsync(Home, "user", true);
    }

    private object State(string? space = null) => new
    {
        active = space == null || Active()?.Space == space ? Active()?.Id : null,
        tabs = browserTabs.Values.Where(tab => space == null || tab.Space == space).Select(tab => new
        {
            id = tab.Id, agent = tab.Agent, space = tab.Space, title = tab.View.CoreWebView2?.DocumentTitle ?? "",
            url = tab.View.Source?.ToString() ?? ""
        }).ToArray()
    };

    private void StartServer()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        connectionFile = Path.Combine(dataDir, "connection.json");
        File.WriteAllText(connectionFile, JsonSerializer.Serialize(new { port, token }));
        _ = ServeAsync(listener, token);
    }

    private async Task ServeAsync(HttpListener server, string token)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = await server.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleRequestAsync(context, token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, string token)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Headers["Authorization"] != $"Bearer {token}")
            {
                await RespondAsync(context, 401, new { error = "Unauthorized" });
                return;
            }
            if (context.Request.ContentLength64 > 1024 * 1024) throw new InvalidOperationException("Request too large");
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            if (json.Length > 1024 * 1024) throw new InvalidOperationException("Request too large");
            using var request = JsonDocument.Parse(json);
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(new Action(async () =>
            {
                try { source.SetResult(await CommandAsync(request.RootElement)); }
                catch (Exception ex) { source.SetException(ex); }
            }));
            await RespondAsync(context, 200, await source.Task);
        }
        catch (Exception ex)
        {
            try { await RespondAsync(context, 400, new { error = ex.Message }); } catch { }
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, object? result)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(result);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
        context.Response.Close();
    }

    private async Task<object?> CommandAsync(JsonElement body)
    {
        var command = Read(body, "command");
        var requestedSpace = Read(body, "space");
        if (command == "save") { SaveTabs(); return new { ok = true }; }
        if (command == "spaces") return spaces.Select(space => new
        {
            name = space, tabs = browserTabs.Values.Count(tab => tab.Space == space)
        }).ToArray();
        if (command == "space")
        {
            if (string.IsNullOrWhiteSpace(requestedSpace) || requestedSpace.Length > 80)
                throw new InvalidOperationException("Space name must be 1-80 characters");
            if (requestedSpace is "user" or "legacy")
                throw new InvalidOperationException("Space name is reserved");
            spaces.Add(requestedSpace);
            return State(requestedSpace);
        }
        if (command == "list") return State(requestedSpace.Length == 0 ? null : requestedSpace);
        if (command == "new")
        {
            if (requestedSpace == "user") throw new InvalidOperationException("User tabs cannot be created by an agent");
            var space = requestedSpace.Length == 0 ? "legacy" : requestedSpace;
            spaces.Add(space);
            return new { id = (await CreateTabAsync(Read(body, "url"), space, false)).Id };
        }
        var tab = browserTabs.GetValueOrDefault(int.TryParse(Read(body, "id"), out var id) ? id : -1)
            ?? throw new InvalidOperationException("Unknown tab");
        if (requestedSpace.Length > 0 && tab.Space != requestedSpace)
            throw new InvalidOperationException("Tab belongs to another space");
        var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("Tab is not ready");
        switch (command)
        {
            case "show": tabs.SelectedTab = tab.Page; return new { id = tab.Id };
            case "goto": return new { url = await NavigateAsync(core, NormalizeUrl(Read(body, "url"))) };
            case "snapshot": return await SnapshotAsync(tab,
                int.TryParse(Read(body, "textLimit"), out var textLimit) ? Math.Clamp(textLimit, 100, 20000) : 4000,
                int.TryParse(Read(body, "elementLimit"), out var elementLimit) ? Math.Clamp(elementLimit, 10, 250) : 80);
            case "click":
            case "fill": return new { ok = await ActAsync(tab, command, Read(body, "target"), Read(body, "value")) };
            case "press": return new { ok = await PressAsync(core, Read(body, "key")) };
            case "scroll": return new { ok = await ScrollAsync(core, Read(body, "direction"), Read(body, "pixels")) };
            case "wait": return new { ok = await WaitAsync(tab, Read(body, "target"), int.TryParse(Read(body, "timeout"), out var ms) ? ms : 10000) };
            case "js": return new { result = JsonNode.Parse(await core.ExecuteScriptAsync(Read(body, "code"))) };
            case "screenshot":
                using (var image = new MemoryStream())
                {
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                    return new { png = Convert.ToBase64String(image.ToArray()) };
                }
            case "close": CloseTab(tab); return new { ok = true };
            default: throw new InvalidOperationException($"Unknown command: {command}");
        }
    }

    private static string Read(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static async Task<string> NavigateAsync(CoreWebView2 core, string url)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDone(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += OnDone;
        try
        {
            core.Navigate(url);
            var success = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!success) throw new InvalidOperationException("Navigation failed");
            return core.Source;
        }
        finally { core.NavigationCompleted -= OnDone; }
    }

    private static async Task<bool> PressAsync(CoreWebView2 core, string key)
    {
        if (key is not ("Enter" or "Tab" or "Escape" or "Backspace" or "ArrowUp" or "ArrowDown" or "ArrowLeft" or "ArrowRight"))
            throw new InvalidOperationException("Unsupported key");
        var script = $$"""
            (() => {
              const key = {{JsonSerializer.Serialize(key)}};
              const el = document.activeElement;
              if (!el) return false;
              const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
              el.dispatchEvent(event);
              if (!event.defaultPrevented && key === 'Enter') {
                if (el.form) el.form.requestSubmit();
                else if (el.tagName === 'A' || el.tagName === 'BUTTON') el.click();
              }
              if (!event.defaultPrevented && key === 'Tab') {
                const focusable = [...document.querySelectorAll('a[href],button,input,textarea,select,[tabindex]:not([tabindex="-1"])')];
                focusable[(focusable.indexOf(el) + 1) % focusable.length]?.focus();
              }
              el.dispatchEvent(new KeyboardEvent('keyup', { key, bubbles: true }));
              return true;
            })()
            """;
        return await core.ExecuteScriptAsync(script) == "true";
    }

    private static async Task<bool> ScrollAsync(CoreWebView2 core, string direction, string pixelsText)
    {
        if (direction is not ("up" or "down")) throw new InvalidOperationException("Direction must be up or down");
        var pixels = int.TryParse(pixelsText, out var parsed) ? Math.Clamp(parsed, 1, 10000) : 600;
        var deltaY = direction == "down" ? pixels : -pixels;
        var result = await core.ExecuteScriptAsync($"(() => {{ window.scrollBy(0, {deltaY}); return window.scrollY; }})()");
        return result != "null";
    }

    private static readonly string SnapshotScript = """
        (() => {
          document.querySelectorAll('[data-ego-native-ref]').forEach(el => el.removeAttribute('data-ego-native-ref'));
          const elements = [...document.querySelectorAll('a,button,input,textarea,select,[role="button"],[contenteditable="true"]')]
            .filter(el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; })
            .slice(0, __ELEMENT_LIMIT__).map((el, i) => {
              const ref = String(i + 1); el.setAttribute('data-ego-native-ref', ref);
              return { ref, tag: el.tagName.toLowerCase(), text: (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '').trim().slice(0, 160), href: el.getAttribute('href') };
            });
          return { title: document.title, url: location.href, text: (document.body?.innerText || '').slice(0, __TEXT_LIMIT__), elements };
        })()
        """;

    private async Task<object> SnapshotAsync(BrowserTab tab, int textLimit, int elementLimit)
    {
        var script = SnapshotScript.Replace("__ELEMENT_LIMIT__", elementLimit.ToString())
            .Replace("__TEXT_LIMIT__", textLimit.ToString());
        var results = new List<(string Frame, JsonNode Data)>();
        var main = JsonNode.Parse(await tab.View.CoreWebView2!.ExecuteScriptAsync(script));
        if (main != null) results.Add(("f0", main));
        foreach (var (id, frame) in tab.Frames.ToArray())
        {
            try
            {
                var data = JsonNode.Parse(await frame.ExecuteScriptAsync(script));
                if (data != null) results.Add((id, data));
            }
            catch { tab.Frames.Remove(id); }
        }
        var combinedText = string.Join("\n", results.Select(r => $"[{r.Frame}] {r.Data["text"]}"));
        return new
        {
            title = main?["title"]?.GetValue<string>() ?? "",
            url = main?["url"]?.GetValue<string>() ?? "",
            text = combinedText[..Math.Min(textLimit, combinedText.Length)],
            elements = results.SelectMany(r => (r.Data["elements"]?.AsArray() ?? []).Select(e => new
            {
                @ref = $"@{r.Frame}:{e?["ref"]}", tag = e?["tag"]?.GetValue<string>(),
                text = e?["text"]?.GetValue<string>(), href = e?["href"]?.GetValue<string>()
            })).Take(elementLimit).ToArray(),
            frames = results.Select(r => new { frame = r.Frame, url = r.Data["url"]?.GetValue<string>() }).ToArray()
        };
    }

    private async Task<bool> ActAsync(BrowserTab tab, string command, string target, string value)
    {
        if (target.Length == 0) throw new InvalidOperationException("Target required");
        var match = System.Text.RegularExpressions.Regex.Match(target, "^@(?<frame>f[0-9]+):(?<ref>[0-9]+)$");
        var frameId = match.Success ? match.Groups["frame"].Value : "f0";
        var selector = match.Success ? "@" + match.Groups["ref"].Value : target;
        var script = $$"""
            (() => {
              const target = {{JsonSerializer.Serialize(selector)}};
              const value = {{JsonSerializer.Serialize(value)}};
              const el = target.startsWith('@')
                ? document.querySelector('[data-ego-native-ref="' + CSS.escape(target.slice(1)) + '"]')
                : document.querySelector(target);
              if (!el) throw new Error('Element not found. Take a new snapshot.');
              el.scrollIntoView({ block: 'center' });
              if ({{JsonSerializer.Serialize(command)}} === 'click') { el.click(); return true; }
              if (!('value' in el)) throw new Error('Element cannot be filled');
              el.focus();
              const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
              if (setter) setter.call(el, value); else el.value = value;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """;
        var result = frameId == "f0"
            ? await tab.View.CoreWebView2!.ExecuteScriptAsync(script)
            : await (tab.Frames.GetValueOrDefault(frameId) ?? throw new InvalidOperationException("Frame changed. Take a new snapshot.")).ExecuteScriptAsync(script);
        if (result == "null") throw new InvalidOperationException("Action failed or frame changed. Take a new snapshot.");
        return result == "true";
    }

    private static async Task<bool> WaitAsync(BrowserTab tab, string selector, int timeout)
    {
        if (selector.Length == 0) throw new InvalidOperationException("CSS selector required");
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(timeout, 100, 30000));
        do
        {
            try
            {
                if (await tab.View.CoreWebView2!.ExecuteScriptAsync($"Boolean(document.querySelector({JsonSerializer.Serialize(selector)}))") == "true") return true;
            }
            catch (InvalidOperationException) { /* Navigation in progress */ }
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Timed out waiting for {selector}");
    }
}
