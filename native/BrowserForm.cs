using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PluckWindowsNative;

public sealed partial class BrowserForm : Form
{
    private sealed class BrowserTab(int id, string space, TabPage page, WebView2 view, string owner)
    {
        public int Id { get; } = id;
        public string Space { get; } = space;
        public bool Agent => Space != "user";
        public TabPage Page { get; } = page;
        public WebView2 View { get; } = view;
        public string Owner { get; } = owner;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public int PendingCommands { get; set; }
        public int Order { get; set; }
        public bool Loading { get; set; }
        public Dictionary<string, CoreWebView2Frame> Frames { get; } = [];
    }

    private sealed record SavedTab(string Url, string Space, bool Active = false);

    private const string Home = "https://www.google.com/";
    private readonly string dataDir = Environment.GetEnvironmentVariable("PLUCK_NATIVE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pluck-windows-native");
    private readonly TabControl tabs = new ContentTabs { Dock = DockStyle.Fill };
    private readonly Dictionary<int, BrowserTab> browserTabs = [];
    private readonly HashSet<string> spaces = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer cleanupTimer = new() { Interval = 60000 };
    private CoreWebView2Environment? environment;
    private HttpListener? listener;
    private string? connectionFile;
    private int nextId = 1;

    public BrowserForm()
    {
        Text = "pluck";
        Width = 1380;
        Height = 900;
        MinimumSize = new Size(780, 520);
        KeyPreview = true;
        BuildChrome();
        historySaveTimer.Tick += SaveHistoryTick;
        tabs.SelectedIndexChanged += (_, _) => RefreshAddress();
        KeyDown += OnShortcut;
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (aiRuns.Count > 0 && e.CloseReason == CloseReason.UserClosing && MessageBox.Show(this,
                "AI가 작업 중입니다. 닫으면 작업이 중단되며 저장된 대화에서 나중에 이어갈 수 있습니다. 닫을까요?",
                "작업 중인 대화", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            { e.Cancel = true; return; }
            SaveTabs();
        };
        cleanupTimer.Tick += (_, _) => CleanupIdleTabs();
        FormClosed += (_, _) =>
        {
            cleanupTimer.Dispose();
            StopAllAssistants();
            FlushAssistantHistory();
            historySaveTimer.Dispose();
            listener?.Close();
            if (connectionFile != null) File.Delete(connectionFile);
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataDir, "profile"));
            await InitializeChromeAsync();
            var saved = ReadSavedTabs();
            var restored = saved.Length == 0 ? [new SavedTab(Home, "user")] : saved;
            for (var i = 0; i < restored.Length; i++)
            {
                if (restored[i].Space != "user") spaces.Add(restored[i].Space);
                await CreateTabAsync(restored[i].Url, restored[i].Space, restored[i].Active || (!restored.Any(tab => tab.Active) && i == 0));
            }
            Active()?.View.Focus();
            StartServer();
            cleanupTimer.Start();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dataDir, "startup-error.log"), ex.ToString());
            MessageBox.Show(this, ex.Message, "pluck Windows Native 시작 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    tab?["url"]?.GetValue<string>() ?? "", tab?["space"]?.GetValue<string>() ?? "user", tab?["active"]?.GetValue<bool>() == true));
            var valid = items.Where(tab => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "file" && !string.IsNullOrWhiteSpace(tab.Space) && tab.Space.Length <= 80)
                .ToArray();
            return valid.Where(tab => tab.Space == "user").Take(50)
                .Concat(valid.Where(tab => tab.Space != "user").GroupBy(tab => tab.Space)
                    .SelectMany(group => group.Take(20)).Take(60)).ToArray();
        }
        catch { return []; }
    }

    private void SaveTabs()
    {
        if (!Directory.Exists(dataDir)) return;
        var saved = OrderedTabs().Select(tab => new { url = tab.View.Source?.ToString(), space = tab.Space, active = tab == Active() })
            .Where(tab => Uri.TryCreate(tab.url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file").ToArray();
        File.WriteAllText(Path.Combine(dataDir, "tabs.json"), JsonSerializer.Serialize(new { tabs = saved }));
    }

    private async Task<BrowserTab> CreateTabAsync(string? url, string space, bool select, bool navigate = true, string owner = "")
    {
        if (environment == null) throw new InvalidOperationException("Browser is not ready");
        if (space != "user" && (browserTabs.Values.Count(tab => tab.Agent) >= 60
            || browserTabs.Values.Count(tab => tab.Space == space) >= 20))
            throw new InvalidOperationException("Agent tab limit reached (20 per space, 60 total). Close unused tabs first.");
        var id = nextId++;
        var page = new TabPage(space == "user" ? "새 탭" : $"◆ {space} {id}") { Padding = Padding.Empty, BackColor = Color.White };
        var view = new WebView2 { Dock = DockStyle.Fill };
        view.KeyDown += OnShortcut;
        page.Controls.Add(view);
        var tab = new BrowserTab(id, space, page, view, owner) { Order = nextOrder++ };
        browserTabs.Add(id, tab);
        tabs.TabPages.Add(page);
        if (select) tabs.SelectedTab = page;
        RefreshSidebar();
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateTitle(tab);
            view.CoreWebView2.SourceChanged += (_, _) => { if (Active() == tab) RefreshAddress(); };
            view.CoreWebView2.NavigationStarting += (_, _) => { tab.Frames.Clear(); tab.Loading = true; RefreshSidebar(); };
            view.CoreWebView2.NavigationCompleted += (_, _) => { tab.Loading = false; if (Active() == tab) RefreshAddress(); else RefreshSidebar(); };
            view.CoreWebView2.FaviconChanged += (_, _) => RefreshSidebar();
            view.CoreWebView2.HistoryChanged += (_, _) => { if (Active() == tab) RefreshNavigation(); };
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
                    var popup = await CreateTabAsync(null, tab.Space, !tab.Agent, navigate: false, owner: tab.Owner);
                    e.NewWindow = popup.View.CoreWebView2;
                    e.Handled = true;
                }
                catch
                {
                    // Agent popups must not escape tab limits into unmanaged windows.
                    e.Handled = tab.Agent;
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
            RefreshSidebar();
            throw;
        }
    }

    private void UpdateTitle(BrowserTab tab)
    {
        var title = tab.View.CoreWebView2?.DocumentTitle;
        tab.Page.Text = (tab.Agent ? $"◆ {tab.Space} · " : "") + (string.IsNullOrWhiteSpace(title) ? "새 탭" : title.Length > 24 ? title[..24] + "…" : title);
        if (Active() == tab) Text = string.IsNullOrWhiteSpace(title) ? "pluck" : $"{title} — pluck";
        RefreshSidebar();
    }

    private BrowserTab? Active() => browserTabs.Values.FirstOrDefault(tab => tab.Page == tabs.SelectedTab);

    private void RefreshAddress()
    {
        var tab = Active();
        var title = tab?.View.CoreWebView2?.DocumentTitle;
        Text = string.IsNullOrWhiteSpace(title) ? "pluck" : $"{title} — pluck";
        RefreshSidebar();
    }

    private static string NormalizeUrl(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0) return Home;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file") return uri.AbsoluteUri;
        if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
        if (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || value.StartsWith("127.0.0.1")) return "http://" + value;
        if (!value.Contains(' ') && value.Contains('.')) return "https://" + value;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
    }

    // WebView2 raises KeyDown for accelerator keys pressed inside a page, so the same handler serves every surface.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; }
    }

    private (Keys Keys, long Tick) lastShortcut;

    private bool HandleShortcut(Keys keys)
    {
        // The chrome page forwards shortcuts itself; drop a duplicate if the WebView reported the same press too.
        if (lastShortcut.Keys == keys && Environment.TickCount64 - lastShortcut.Tick < 80) return true;
        lastShortcut = (keys, Environment.TickCount64);
        var key = keys & Keys.KeyCode;
        var ctrl = (keys & Keys.Control) != 0;
        var shift = (keys & Keys.Shift) != 0;
        var alt = (keys & Keys.Alt) != 0;
        var ordered = OrderedTabs();
        var index = Array.IndexOf(ordered, Active());
        if (ctrl && !alt && key == Keys.T && shift) ReopenClosedTab();
        else if (ctrl && !alt && key == Keys.T) _ = OpenPaletteAsync("new");
        else if (ctrl && !alt && !shift && key == Keys.L) _ = OpenPaletteAsync("current");
        else if (ctrl && !alt && !shift && key == Keys.K) _ = OpenPaletteAsync("tabs");
        else if (ctrl && !alt && !shift && key == Keys.W) { if (Active() is { } tab) CloseTab(tab); }
        else if (ctrl && !alt && shift && key == Keys.S) { _ = AnimateSidebarAsync(!sidebarTarget); }
        else if (ctrl && !alt && !shift && key == Keys.J) { chromeView.Focus(); PostChrome(new { type = "assist" }); }
        else if (ctrl && !alt && key == Keys.Tab && ordered.Length > 0)
            ShowTab(ordered[(index + (shift ? ordered.Length - 1 : 1) + ordered.Length) % ordered.Length]);
        else if (ctrl && !alt && !shift && key is >= Keys.D1 and <= Keys.D9)
        {
            var users = ordered.Where(tab => !tab.Agent).ToArray();
            var n = key == Keys.D9 ? users.Length - 1 : key - Keys.D1;
            if (n >= 0 && n < users.Length) ShowTab(users[n]);
        }
        else if ((ctrl && !alt && !shift && key == Keys.R) || (!ctrl && !alt && key == Keys.F5)) Active()?.View.Reload();
        else if (alt && !ctrl && key == Keys.Left) GoBack();
        else if (alt && !ctrl && key == Keys.Right) GoForward();
        else return false;
        return true;
    }

    // Selecting a tab on the person's behalf also gives the page keyboard focus, as other browsers do.
    private void ShowTab(BrowserTab tab)
    {
        tabs.SelectedTab = tab.Page;
        if (!paletteOpen) tab.View.Focus();
    }

    private void GoBack() { if (Active()?.View.CanGoBack == true) Active()!.View.GoBack(); }
    private void GoForward() { if (Active()?.View.CanGoForward == true) Active()!.View.GoForward(); }

    private void ReopenClosedTab()
    {
        if (closedTabs.TryPop(out var url)) _ = CreateTabAsync(url, "user", true);
    }

    private void CloseTab(BrowserTab tab)
    {
        if (!browserTabs.ContainsKey(tab.Id)) return;
        if (Active() == tab)
        {
            // Select a neighbour from the sidebar order instead of whatever TabControl picks.
            var ordered = OrderedTabs();
            var index = Array.IndexOf(ordered, tab);
            var next = ordered.Skip(index + 1).FirstOrDefault(other => other.Space == tab.Space)
                ?? ordered.Take(index).LastOrDefault(other => other.Space == tab.Space)
                ?? ordered.FirstOrDefault(other => other != tab && !other.Agent)
                ?? ordered.FirstOrDefault(other => other != tab);
            if (next != null) tabs.SelectedTab = next.Page;
        }
        browserTabs.Remove(tab.Id);
        if (!tab.Agent && tab.View.Source is { } source && source.Scheme is "http" or "https") closedTabs.Push(source.ToString());
        tabs.TabPages.Remove(tab.Page);
        tab.Frames.Clear();
        tab.View.Dispose();
        tab.Page.Dispose();
        if (tab.Agent && !browserTabs.Values.Any(other => other.Space == tab.Space)) spaces.Remove(tab.Space);
        RefreshSidebar();
        if (browserTabs.Count == 0) _ = CreateTabAsync(Home, "user", true);
    }

    private void CleanupIdleTabs()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var group in browserTabs.Values.Where(tab => tab.Agent).GroupBy(tab => tab.Space).ToArray())
        {
            if (group.Any(tab => tab.PendingCommands > 0 || tab.LastUsed > cutoff || tab == Active())) continue;
            foreach (var tab in group.ToArray()) CloseTab(tab);
        }
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
            if (Read(request.RootElement, "command") == "shutdown") BeginInvoke(new Action(Close));
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
        var owner = Read(body, "owner");
        if (command is "cleanup-run" or "heartbeat")
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("Run owner required");
            var owned = browserTabs.Values.Where(tab => tab.Agent && tab.Owner == owner).ToArray();
            foreach (var ownedTab in owned)
                if (command == "cleanup-run") CloseTab(ownedTab);
                else ownedTab.LastUsed = DateTime.UtcNow;
            return new { ok = true, count = owned.Length };
        }
        if (command == "shutdown")
        {
            if (aiRuns.Count > 0) throw new InvalidOperationException("AI 작업 중에는 자동 종료할 수 없습니다. 작업 완료 후 다시 시도하세요.");
            return new { ok = true };
        }
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
        if (command == "list")
        {
            foreach (var listedTab in browserTabs.Values.Where(tab => tab.Space == requestedSpace))
                listedTab.LastUsed = DateTime.UtcNow;
            return State(requestedSpace.Length == 0 ? null : requestedSpace);
        }
        if (command == "active")
        {
            var active = Active();
            return new
            {
                id = active?.Id, space = active?.Space, title = active?.View.CoreWebView2?.DocumentTitle ?? "",
                url = active?.View.Source?.ToString() ?? ""
            };
        }
        if (command == "new")
        {
            if (requestedSpace == "user") throw new InvalidOperationException("User tabs cannot be created by an agent");
            var space = requestedSpace.Length == 0 ? "legacy" : requestedSpace;
            spaces.Add(space);
            return new { id = (await CreateTabAsync(Read(body, "url"), space, false, owner: owner)).Id };
        }
        var tab = browserTabs.GetValueOrDefault(int.TryParse(Read(body, "id"), out var id) ? id : -1)
            ?? throw new InvalidOperationException("Unknown tab");
        if (requestedSpace.Length > 0 && tab.Space != requestedSpace)
            throw new InvalidOperationException("Tab belongs to another space");
        if (command == "close") { CloseTab(tab); return new { ok = true }; }
        var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("Tab is not ready");
        tab.LastUsed = DateTime.UtcNow;
        tab.PendingCommands++;
        RefreshSidebar();
        try
        {
        if (command != "screenshot")
        {
            if (followAssistant && aiRuns.Count > 0) ShowTab(tab);
            PostChrome(new { type = "activity", running = true, label = ActivityLabel(command) });
            await ShowActivityAsync(tab, command, command == "press" ? ":focus" : "");
        }
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
            default: throw new InvalidOperationException($"Unknown command: {command}");
        }
        }
        finally
        {
            tab.PendingCommands--; tab.LastUsed = DateTime.UtcNow; RefreshSidebar();
            if (command != "screenshot")
            {
                await ShowActivityAsync(tab, command, done: true);
                PostChrome(new { type = "activity", running = browserTabs.Values.Any(t => t.PendingCommands > 0), label = ActivityLabel(command) });
            }
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
          document.querySelectorAll('[data-pluck-native-ref]').forEach(el => el.removeAttribute('data-pluck-native-ref'));
          const elements = [...document.querySelectorAll('a,button,input,textarea,select,[role="button"],[contenteditable="true"]')]
            .filter(el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; })
            .slice(0, __ELEMENT_LIMIT__).map((el, i) => {
              const ref = String(i + 1); el.setAttribute('data-pluck-native-ref', ref);
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
        await ShowActivityAsync(tab, command, selector, frameId);
        // Only a visible click needs a brief lead-in before navigation can replace the page.
        if (command == "click" && Active() == tab) await Task.Delay(90);
        var script = $$"""
            (() => {
              const target = {{JsonSerializer.Serialize(selector)}};
              const value = {{JsonSerializer.Serialize(value)}};
              const el = target.startsWith('@')
                ? document.querySelector('[data-pluck-native-ref="' + CSS.escape(target.slice(1)) + '"]')
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
        await ShowActivityAsync(tab, command, selector, frameId, done: true);
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
