using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OinziBrowser;

public sealed partial class BrowserForm : Form
{
    private sealed class BrowserTab(int id, string space, TabPage page, WebView2 view, string owner, string profile)
    {
        // Browser profile (cookie jar) the tab was created in; fixed for its lifetime.
        public string Profile { get; } = profile;
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
        // Latest overview thumbnail, and the capture still running for it; hidden tabs can take seconds to yield a frame.
        public string? Thumb { get; set; }
        public Task<string?>? ThumbCapture { get; set; }
        public DateTime ThumbStarted { get; set; }
        public bool ThumbDue { get; set; }
        // Dialogs answered on the agent's behalf since its last snapshot, so it learns what an alert said.
        public List<string> Dialogs { get; } = [];
        // What the last snapshot showed, so a diff snapshot can return only what changed.
        public HashSet<string>? LastSnapshotKeys { get; set; }
        public int LastSnapshotText { get; set; }
        public string LastSnapshotUrl { get; set; } = "";
        // Counts cross-document navigations, so per-document flags below reset when the page is replaced.
        public int DocumentSerial { get; set; }
        // Document an agent ran js in, and document Oinzi typed a saved password into (with what it typed, for masking).
        public int ScriptedDocument { get; set; } = -1;
        public int LoginDocument { get; set; } = -1;
        public List<string> LoginSecrets { get; } = [];
    }

    // One AI panel conversation or one terminal agent. Its tabs are invisible to every other session.
    private sealed class AgentSession(string id, string label, string kind)
    {
        public string Id { get; } = id;
        public string Label { get; set; } = label;
        public string Kind { get; set; } = kind;
        public int Order { get; init; }
        // The person's tabs this session may drive, granted only by reading the tab they are looking at.
        public HashSet<int> UserTabs { get; } = [];
        // Last command or heartbeat; a terminal agent counts as working while this is recent.
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public string Activity { get; set; } = "";
        // One line the agent writes about where it stands ("검증 중", "막힘: 캡차"), shown on its overview card.
        public string Note { get; set; } = "";
        // Set from the overview; a terminal agent cannot be killed, so its further commands are refused instead.
        public bool Stopped { get; set; }
        // Account workspace its tabs open in unless a command names another, so sessions can work as different accounts at once.
        public string Profile { get; set; } = DefaultProfile;
    }

    private sealed record SavedTab(string Url, string Space, bool Active = false, string Profile = "default");

    private const string Home = "https://www.google.com/";
    private static readonly System.Text.RegularExpressions.Regex SessionIdPattern = new("^[A-Za-z0-9_-]{6,64}$");
    private readonly string dataDir = DataDirectory;
    // What the person opens themselves (exported videos, AI output) lives in Documents\Oinzi; profile, history
    // and raw recording frames stay in dataDir because they hold login state and everything that was on screen.
    private static readonly string userFilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Oinzi");
    private static string RecordingVideosDir => Path.Combine(userFilesDir, "녹화");
    private static string AiWorkDir => Path.Combine(userFilesDir, "AI 작업");
    private static string ScreenshotsDir => Path.Combine(userFilesDir, "스크린샷");
    private readonly TabControl tabs = new ContentTabs { Dock = DockStyle.Fill };
    private readonly Dictionary<int, BrowserTab> browserTabs = [];
    private readonly Dictionary<string, AgentSession> sessions = new(StringComparer.Ordinal);
    private int nextSessionOrder = 1;
    private readonly System.Windows.Forms.Timer cleanupTimer = new() { Interval = 60000 };
    private CoreWebView2Environment? environment;
    private HttpListener? listener;
    private string? connectionFile;
    private int nextId = 1;

    public BrowserForm()
    {
        Text = "Oinzi";
        Width = 1380;
        Height = 900;
        MinimumSize = new Size(780, 520);
        KeyPreview = true;
        BuildChrome();
        historySaveTimer.Tick += SaveHistoryTick;
        tabs.SelectedIndexChanged += (_, _) => { FollowWorkspace(); RefreshAddress(); };
        KeyDown += OnShortcut;
        Shown += async (_, _) => await InitializeAsync();
        // Coming back from Windows Settings after choosing the default browser.
        Activated += (_, _) => { if (chromeReady && paletteOpen) PostSetupState(); };
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
            StopAllRecordings();
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
            LoadPrefs();
            LoadProfiles();
            LoadSchedules();
            ListenForLinks();
            try { foreach (var dir in new[] { RecordingVideosDir, AiWorkDir, ScreenshotsDir }) Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "profile"),
                new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
            await InitializeChromeAsync();
            ApplyProfilePrefs();
            foreach (var (id, label, kind, profile) in ReadSavedSessions()) RestoreSession(id, label, kind, profile);
            var saved = ReadSavedTabs();
            // "Start with the home page" drops the person's tabs only; agent sessions keep theirs so their work can continue.
            if (prefs.Startup == "home") saved = [new SavedTab(HomeUrl, "user", true), .. saved.Where(tab => tab.Space != "user").Select(tab => tab with { Active = false })];
            var restored = saved.Length == 0 ? [new SavedTab(HomeUrl, "user")] : saved;
            for (var i = 0; i < restored.Length; i++)
            {
                if (restored[i].Space != "user") RestoreSession(restored[i].Space, restored[i].Space, "terminal", restored[i].Profile);
                await CreateTabAsync(restored[i].Url, restored[i].Space, restored[i].Active || (!restored.Any(tab => tab.Active) && i == 0),
                    profile: FindProfile(restored[i].Profile)?.Id ?? DefaultProfile);
            }
            await OpenLaunchLinksAsync();
            Active()?.View.Focus();
            StartServer();
            cleanupTimer.Start();
            try { RefreshBrowserRegistration(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            if (!prefs.SetupOffered) _ = OfferSetupAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dataDir, "startup-error.log"), ex.ToString());
            MessageBox.Show(this, ex.Message, "Oinzi Windows Native 시작 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    tab?["url"]?.GetValue<string>() ?? "", tab?["space"]?.GetValue<string>() ?? "user", tab?["active"]?.GetValue<bool>() == true,
                    tab?["profile"]?.GetValue<string>() ?? DefaultProfile));
            var valid = items.Where(tab => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "file" && !string.IsNullOrWhiteSpace(tab.Space) && tab.Space.Length <= 80)
                .ToArray();
            return valid.Where(tab => tab.Space == "user").Take(50)
                .Concat(valid.Where(tab => tab.Space != "user").GroupBy(tab => tab.Space)
                    .SelectMany(group => group.Take(20)).Take(60)).ToArray();
        }
        catch { return []; }
    }

    private (string Id, string Label, string Kind, string Profile)[] ReadSavedSessions()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "tabs.json")));
            return (root is JsonObject ? root["sessions"]?.AsArray() ?? [] : [])
                .Select(item => (Id: item?["id"]?.GetValue<string>() ?? "", Label: item?["label"]?.GetValue<string>() ?? "", Kind: item?["kind"]?.GetValue<string>() ?? "terminal",
                    Profile: item?["profile"]?.GetValue<string>() ?? DefaultProfile))
                .Where(item => item.Id is not ("" or "user") && item.Id.Length <= 80 && item.Label.Length > 0).ToArray();
        }
        catch { return []; }
    }

    private void SaveTabs()
    {
        if (!Directory.Exists(dataDir)) return;
        var saved = OrderedTabs().Select(tab => new { url = tab.View.Source?.ToString(), space = tab.Space, active = tab == Active(), profile = tab.Profile })
            .Where(tab => Uri.TryCreate(tab.url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file").ToArray();
        var savedSessions = sessions.Values.Where(session => saved.Any(tab => tab.space == session.Id)).OrderBy(session => session.Order)
            .Select(session => new { id = session.Id, label = session.Label, kind = session.Kind, profile = session.Profile }).ToArray();
        File.WriteAllText(Path.Combine(dataDir, "tabs.json"), JsonSerializer.Serialize(new { tabs = saved, sessions = savedSessions }));
    }

    // Restored sessions keep whatever id they were saved with, including names from before sessions had ids.
    private void RestoreSession(string id, string label, string kind, string profile)
    {
        if (!sessions.ContainsKey(id))
            sessions[id] = new AgentSession(id, label, kind) { Order = nextSessionOrder++, Profile = FindProfile(profile)?.Id ?? DefaultProfile };
    }

    private AgentSession OpenSession(string id, string? label = null, string kind = "terminal")
    {
        if (!sessions.TryGetValue(id, out var session))
        {
            if (id is "user" or "legacy" || !SessionIdPattern.IsMatch(id)) throw new InvalidOperationException("Invalid session id");
            // A new session works in the workspace the person is looking at until told otherwise.
            sessions[id] = session = new AgentSession(id, "세션 " + id[^6..], kind) { Order = nextSessionOrder++, Profile = userProfile };
        }
        if (!string.IsNullOrWhiteSpace(label) && Clip(label.Trim(), 60) is var name && name != session.Label)
        {
            session.Label = name;
            RefreshSidebar();
        }
        return session;
    }

    private static string NewSessionId() => "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private string SessionLabel(string space) => sessions.GetValueOrDefault(space)?.Label ?? space;

    private async Task<BrowserTab> CreateTabAsync(string? url, string space, bool select, bool navigate = true, string owner = "", string? profile = null)
    {
        profile ??= space == "user" ? userProfile : DefaultProfile;
        if (environment == null) throw new InvalidOperationException("Browser is not ready");
        if (space != "user" && (browserTabs.Values.Count(tab => tab.Agent) >= 60
            || browserTabs.Values.Count(tab => tab.Space == space) >= 20))
            throw new InvalidOperationException("Agent tab limit reached (20 per space, 60 total). Close unused tabs first.");
        var id = nextId++;
        var page = new TabPage(space == "user" ? "새 탭" : $"◆ {SessionLabel(space)} {id}") { Padding = Padding.Empty, BackColor = Color.White };
        var view = new PageView { Dock = DockStyle.Fill };
        view.KeyDown += OnShortcut;
        page.Controls.Add(view);
        var tab = new BrowserTab(id, space, page, view, owner, profile) { Order = nextOrder++ };
        browserTabs.Add(id, tab);
        tabs.TabPages.Add(page);
        if (select) tabs.SelectedTab = page;
        RefreshSidebar();
        try
        {
            if (ControllerOptions(profile) is { } options) await view.EnsureCoreWebView2Async(environment, options);
            else await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateTitle(tab);
            view.CoreWebView2.SourceChanged += (_, _) => { if (Active() == tab) RefreshAddress(); };
            view.CoreWebView2.NavigationStarting += (_, _) =>
            {
                tab.Frames.Clear(); tab.Loading = true; tab.DocumentSerial++; tab.LoginSecrets.Clear(); RefreshSidebar();
            };
            view.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                tab.Loading = false;
                if (Active() == tab) RefreshAddress(); else RefreshSidebar();
                ScheduleThumbnail(tab);
            };
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
                    // A popup must live in its opener's profile, or WebView2 refuses to attach it.
                    var popup = await CreateTabAsync(null, tab.Space, !tab.Agent, navigate: false, owner: tab.Owner, profile: tab.Profile);
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
            // A native "leave site?" or alert box blocks the page until someone clicks it, and agents cannot click it.
            // Built-in dialogs are off everywhere; agent work accepts them, the person still gets a real prompt.
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            ApplyTabPrefs(view.CoreWebView2);
            view.CoreWebView2.ScriptDialogOpening += (_, e) => AnswerDialog(tab, e);
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
        tab.Page.Text = (tab.Agent ? $"◆ {SessionLabel(tab.Space)} · " : "") + (string.IsNullOrWhiteSpace(title) ? "새 탭" : title.Length > 24 ? title[..24] + "…" : title);
        if (Active() == tab) Text = string.IsNullOrWhiteSpace(title) ? "Oinzi" : $"Oinzi — {title}";
        RefreshSidebar();
    }

    private BrowserTab? Active() => browserTabs.Values.FirstOrDefault(tab => tab.Page == tabs.SelectedTab);

    private void RefreshAddress()
    {
        var tab = Active();
        var title = tab?.View.CoreWebView2?.DocumentTitle;
        Text = string.IsNullOrWhiteSpace(title) ? "Oinzi" : $"Oinzi — {title}";
        RefreshSidebar();
        _ = PostToolbarExtensionsAsync();
    }

    private static string NormalizeUrl(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0) return HomeUrl;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file") return uri.AbsoluteUri;
        if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
        if (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || value.StartsWith("127.0.0.1")) return "http://" + value;
        if (!value.Contains(' ') && value.Contains('.')) return "https://" + value;
        return SearchUrl(value);
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
        var ordered = OrderedTabs().Where(InWorkspace).ToArray();
        var index = Array.IndexOf(ordered, Active());
        if (ctrl && !alt && key == Keys.T && shift) ReopenClosedTab();
        else if (ctrl && !alt && key == Keys.T) _ = OpenPaletteAsync("new");
        else if (ctrl && !alt && !shift && key == Keys.L) _ = OpenPaletteAsync("current");
        else if (ctrl && !alt && !shift && key == Keys.K) _ = OpenPaletteAsync("tabs");
        else if (ctrl && !alt && !shift && key == Keys.W) { if (Active() is { } tab) CloseTab(tab); }
        else if (ctrl && !alt && shift && key == Keys.S) { _ = AnimateSidebarAsync(!sidebarTarget); }
        else if (ctrl && !alt && shift && key == Keys.O) _ = OpenOverviewAsync();
        else if (ctrl && !alt && !shift && key == Keys.Oemcomma) _ = OpenSettingsAsync("");
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
        if (closedTabs.TryPop(out var closed)) _ = CreateTabAsync(closed.Url, "user", true, profile: FindProfile(closed.Profile)?.Id ?? DefaultProfile);
    }

    private void CloseTab(BrowserTab tab)
    {
        if (!browserTabs.ContainsKey(tab.Id)) return;
        if (Active() == tab)
        {
            // Select a neighbour from the sidebar order instead of whatever TabControl picks.
            var ordered = OrderedTabs();
            var index = Array.IndexOf(ordered, tab);
            bool Neighbour(BrowserTab other) => other.Space == tab.Space && (tab.Agent || other.Profile == tab.Profile);
            var next = ordered.Skip(index + 1).FirstOrDefault(Neighbour)
                ?? ordered.Take(index).LastOrDefault(Neighbour)
                ?? ordered.FirstOrDefault(other => other != tab && !other.Agent)
                ?? ordered.FirstOrDefault(other => other != tab);
            if (next != null) tabs.SelectedTab = next.Page;
        }
        browserTabs.Remove(tab.Id);
        if (!tab.Agent && tab.View.Source is { } source && source.Scheme is "http" or "https") closedTabs.Push((source.ToString(), tab.Profile));
        tabs.TabPages.Remove(tab.Page);
        tab.Frames.Clear();
        tab.View.Dispose();
        tab.Page.Dispose();
        if (tab.Agent && !browserTabs.Values.Any(other => other.Space == tab.Space) && sessions.GetValueOrDefault(tab.Space)?.Stopped != true)
            sessions.Remove(tab.Space);
        RefreshSidebar();
        if (browserTabs.Count == 0) _ = CreateTabAsync(HomeUrl, "user", true);
    }

    private void CleanupIdleTabs()
    {
        if (prefs.AgentIdleMinutes <= 0) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-prefs.AgentIdleMinutes);
        foreach (var group in browserTabs.Values.Where(tab => tab.Agent).GroupBy(tab => tab.Space).ToArray())
        {
            if (group.Any(tab => tab.PendingCommands > 0 || tab.LastUsed > cutoff || tab == Active())) continue;
            foreach (var tab in group.ToArray()) CloseTab(tab);
        }
    }

    private object State(string space) => new
    {
        active = Active()?.Space == space ? Active()?.Id : null,
        tabs = SessionTabs(space)
    };

    private object[] SessionTabs(string space) => browserTabs.Values.Where(tab => tab.Space == space).Select(tab => (object)new
    {
        id = tab.Id, agent = tab.Agent, space = tab.Space, title = tab.View.CoreWebView2?.DocumentTitle ?? "",
        url = tab.View.Source?.ToString() ?? "", profile = ProfileName(tab.Profile)
    }).ToArray();

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
        // A fixed path for MCP clients to register; refreshed every start so it matches this build's API.
        try { File.WriteAllText(Path.Combine(dataDir, "mcp.mjs"), ReadResource("mcp.mjs")); } catch (IOException) { }
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
        var owner = Read(body, "owner");
        if (command is "cleanup-run" or "heartbeat")
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("Run owner required");
            var owned = browserTabs.Values.Where(tab => tab.Agent && tab.Owner == owner).ToArray();
            foreach (var ownedTab in owned)
                // A run that failed because the person stopped or took over its session leaves its tabs for them.
                if (command == "cleanup-run") { if (sessions.GetValueOrDefault(ownedTab.Space)?.Stopped != true) CloseTab(ownedTab); }
                else ownedTab.LastUsed = DateTime.UtcNow;
            if (command == "heartbeat" && sessions.GetValueOrDefault(Read(body, "session")) is { } beating) beating.LastSeen = DateTime.UtcNow;
            return new { ok = true, count = owned.Length };
        }
        if (command == "release")
        {
            // An agent.mjs run ended, so the overview stops showing its session as working.
            if (sessions.GetValueOrDefault(Read(body, "session")) is { } released) released.LastSeen = DateTime.MinValue;
            return new { ok = true };
        }
        if (command == "shutdown")
        {
            if (aiRuns.Count > 0) throw new InvalidOperationException("AI 작업 중에는 자동 종료할 수 없습니다. 작업 완료 후 다시 시도하세요.");
            return new { ok = true };
        }
        if (command == "save") { SaveTabs(); return new { ok = true }; }

        // Everything else acts for exactly one session, so concurrent agents never see or touch each other's tabs.
        var requested = Read(body, "session");
        if (command == "session")
        {
            var created = requested.Length == 0;
            var opened = OpenSession(created ? NewSessionId() : requested, Read(body, "label"));
            if (opened.Stopped) throw new InvalidOperationException(StoppedMessage);
            opened.LastSeen = DateTime.UtcNow;
            if (Read(body, "profile") is { Length: > 0 } pinned && pinned != opened.Profile)
            {
                opened.Profile = FindProfile(pinned)?.Id
                    ?? throw new InvalidOperationException($"Unknown profile \"{pinned}\". Profiles: {string.Join(", ", profileBook.Items.Select(p => p.Name))}");
                RefreshSidebar();
            }
            return new { session = opened.Id, label = opened.Label, created, profile = ProfileName(opened.Profile), tabs = SessionTabs(opened.Id) };
        }
        if (requested.Length == 0) throw new InvalidOperationException("Session required. Run through agent.mjs, or set OINZI_SESSION for cli.ps1.");
        var session = OpenSession(requested);
        if (session.Stopped) throw new InvalidOperationException(StoppedMessage);
        session.LastSeen = DateTime.UtcNow;
        if (command == "status")
        {
            session.Note = Clip(Read(body, "text").Trim(), 80);
            return new { ok = true };
        }
        if (command == "batch") return await BatchAsync(body, session.Id, owner);
        if (command == "list")
        {
            foreach (var listedTab in browserTabs.Values.Where(tab => tab.Space == session.Id))
                listedTab.LastUsed = DateTime.UtcNow;
            return State(session.Id);
        }
        if (command == "active")
        {
            var active = Active() ?? throw new InvalidOperationException("No active tab");
            if (active.Space != "user" && active.Space != session.Id)
                throw new InvalidOperationException("The person is looking at another session's tab");
            if (active.Space == "user") session.UserTabs.Add(active.Id);
            return new
            {
                id = active.Id, space = active.Space, title = active.View.CoreWebView2?.DocumentTitle ?? "",
                url = active.View.Source?.ToString() ?? "", profile = ProfileName(active.Profile)
            };
        }
        if (command == "profiles") return new { profiles = ProfilesForAgent(), sessionProfile = ProfileName(session.Profile) };
        if (command == "new")
        {
            var wanted = Read(body, "profile");
            var profile = wanted.Length == 0 ? FindProfile(session.Profile)?.Id ?? DefaultProfile : FindProfile(wanted)?.Id
                ?? throw new InvalidOperationException($"Unknown profile \"{wanted}\". Profiles: {string.Join(", ", profileBook.Items.Select(p => p.Name))}");
            var created = await CreateTabAsync(Read(body, "url"), session.Id, false, owner: owner, profile: profile);
            return new { id = created.Id, profile = ProfileName(profile) };
        }
        var tab = browserTabs.GetValueOrDefault(int.TryParse(Read(body, "id"), out var id) ? id : -1)
            ?? throw new InvalidOperationException("Unknown tab");
        var userTab = tab.Space == "user";
        if (userTab ? !session.UserTabs.Contains(tab.Id) : tab.Space != session.Id)
            throw new InvalidOperationException("Tab belongs to another session");
        if (command == "close")
        {
            if (userTab) throw new InvalidOperationException("The person's tabs cannot be closed by an agent");
            CloseTab(tab);
            return new { ok = true };
        }
        var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("Tab is not ready");
        // Only the conversation the person is watching in the AI panel may move their view or status line.
        var followed = followAssistant && session.Id == FollowedSpace();
        tab.LastUsed = DateTime.UtcNow;
        tab.PendingCommands++;
        RefreshSidebar();
        try
        {
        if (command != "screenshot")
        {
            session.Activity = ActivityLabel(command);
            await RecordActionAsync(session.Id, command, tab, Read(body, "target"),
                command switch { "goto" => Read(body, "url"), "press" => Read(body, "key"), "scroll" => Read(body, "direction"), _ => Read(body, "value") });
        }
        if (command != "screenshot")
        {
            // Following moves the view only; focus stays wherever the person is typing, such as the AI panel.
            if (followed && !userTab && tabs.SelectedTab != tab.Page) tabs.SelectedTab = tab.Page;
            if (followed) PostChrome(new { type = "activity", running = true, label = ActivityLabel(command) });
            await ShowActivityAsync(tab, command, command == "press" ? ":focus" : "");
        }
        GuardLoginDocument(tab, command, Read(body, "target"));
        switch (command)
        {
            case "show": return new { id = tab.Id, shown = followed };
            case "goto": return new { url = await NavigateAsync(core, NormalizeUrl(Read(body, "url"))) };
            case "snapshot": return MaskSecrets(tab, await SnapshotAsync(tab,
                int.TryParse(Read(body, "textLimit"), out var textLimit) ? Math.Clamp(textLimit, 100, 20000) : 4000,
                int.TryParse(Read(body, "elementLimit"), out var elementLimit) ? Math.Clamp(elementLimit, 10, 250) : 80,
                Flag(body, "diff")));
            case "click":
            case "fill":
            case "hover":
            case "select": return await ActAsync(tab, command, Read(body, "target"), Read(body, "value"));
            case "clickAt":
                if (!double.TryParse(Read(body, "x"), System.Globalization.CultureInfo.InvariantCulture, out var atX)
                    || !double.TryParse(Read(body, "y"), System.Globalization.CultureInfo.InvariantCulture, out var atY))
                    throw new InvalidOperationException("x and y required (CSS pixels from the viewport's top-left)");
                await MouseClickAsync(core, atX, atY, Flag(body, "double") ? 2 : 1);
                return new { ok = true };
            case "type":
                await CdpAsync(core, "Input.insertText", new { text = Read(body, "value") });
                return new { ok = true };
            case "press": await PressKeyAsync(core, Read(body, "key")); return new { ok = true };
            case "scroll": return new { ok = await WheelAsync(core, Read(body, "direction"), Read(body, "pixels")) };
            case "back":
            case "forward":
                if (command == "back" ? !core.CanGoBack : !core.CanGoForward) throw new InvalidOperationException($"Cannot go {command}");
                if (command == "back") core.GoBack(); else core.GoForward();
                await Task.Delay(400);
                return new { url = core.Source };
            case "wait": return new { ok = await WaitAsync(tab, Read(body, "target"), int.TryParse(Read(body, "timeout"), out var ms) ? ms : 10000) };
            case "js":
                tab.ScriptedDocument = tab.DocumentSerial;
                return new { result = JsonNode.Parse(await core.ExecuteScriptAsync(Read(body, "code"))) };
            case "screenshot": return new { png = await CaptureAsync(core, Flag(body, "annotate")) };
            case "login": return await LoginAsync(tab, Read(body, "credential"));
            case "compare":
                return await CompareAsync(core, Read(body, "baseline"),
                    double.TryParse(Read(body, "threshold"), System.Globalization.CultureInfo.InvariantCulture, out var threshold) ? threshold : 0.1, Flag(body, "diff"));
            default: throw new InvalidOperationException($"Unknown command: {command}");
        }
        }
        finally
        {
            tab.PendingCommands--; tab.LastUsed = DateTime.UtcNow; RefreshSidebar();
            ScheduleThumbnail(tab);
            if (recordings.TryGetValue(session.Id, out var recording)) _ = CaptureRecordingFrameAsync(recording);
            if (command != "screenshot")
            {
                await ShowActivityAsync(tab, command, done: true);
                if (followed)
                    PostChrome(new { type = "activity", running = browserTabs.Values.Any(t => t.Space == session.Id && t.PendingCommands > 0), label = ActivityLabel(command) });
            }
        }
    }

    private void AnswerDialog(BrowserTab tab, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        if (tab.Agent || tab.PendingCommands > 0)
        {
            if (e.Kind == CoreWebView2ScriptDialogKind.Prompt) e.ResultText = e.DefaultText;
            e.Accept();
            if (tab.Dialogs.Count < 20)
                tab.Dialogs.Add(e.Kind == CoreWebView2ScriptDialogKind.Beforeunload ? "beforeunload: left the page without saving" : $"{e.Kind.ToString().ToLowerInvariant()}: {e.Message}");
            return;
        }
        // Modal UI inside a WebView2 event re-enters the control, so answer after the event returns.
        var deferral = e.GetDeferral();
        BeginInvoke(() =>
        {
            try { AskPerson(e); }
            finally { deferral.Complete(); }
        });
    }

    private void AskPerson(CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var title = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Host : "Oinzi";
        switch (e.Kind)
        {
            case CoreWebView2ScriptDialogKind.Alert:
                MessageBox.Show(this, e.Message, title, MessageBoxButtons.OK);
                e.Accept();
                break;
            case CoreWebView2ScriptDialogKind.Confirm:
                if (MessageBox.Show(this, e.Message, title, MessageBoxButtons.OKCancel) == DialogResult.OK) e.Accept();
                break;
            case CoreWebView2ScriptDialogKind.Beforeunload:
                if (MessageBox.Show(this, "변경한 내용이 저장되지 않을 수 있습니다.", "사이트에서 나갈까요?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK) e.Accept();
                break;
            case CoreWebView2ScriptDialogKind.Prompt:
                if (PromptText(title, e.Message, e.DefaultText) is { } text) { e.ResultText = text; e.Accept(); }
                break;
        }
    }

    private string? PromptText(string title, string message, string value)
    {
        using var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(380, 130) };
        var label = new Label { Text = message, AutoEllipsis = true, Bounds = new Rectangle(12, 12, 356, 36) };
        var input = new TextBox { Text = value, Bounds = new Rectangle(12, 52, 356, 24) };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Bounds = new Rectangle(212, 92, 75, 26) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(293, 92, 75, 26) };
        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private static string[] DrainDialogs(BrowserTab tab)
    {
        var dialogs = tab.Dialogs.ToArray();
        tab.Dialogs.Clear();
        return dialogs;
    }

    private static bool Flag(JsonElement body, string name) => Read(body, name).Equals("true", StringComparison.OrdinalIgnoreCase);

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

    // Interactive elements with their accessible role, name and state, visible ones first, plus a heading outline.
    private static readonly string SnapshotScript = """
        (() => {
          document.querySelectorAll('[data-oinzi-ref]').forEach(el => el.removeAttribute('data-oinzi-ref'));
          const selector = 'a[href],button,input:not([type=hidden]),textarea,select,summary,[contenteditable=""],[contenteditable="true"],'
            + '[role=button],[role=link],[role=checkbox],[role=radio],[role=switch],[role=tab],[role=menuitem],[role=menuitemcheckbox],'
            + '[role=option],[role=combobox],[role=textbox],[role=searchbox],[role=slider],[role=treeitem],[onclick],[tabindex]:not([tabindex="-1"])';
          const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const inView = el => { const r = el.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; };
          const clean = t => (t || '').replace(/\s+/g, ' ').trim();
          const role = el => {
            const explicit = el.getAttribute('role'); if (explicit) return explicit;
            const tag = el.tagName.toLowerCase(), type = (el.getAttribute('type') || 'text').toLowerCase();
            if (tag === 'a') return 'link';
            if (tag === 'button' || tag === 'summary') return 'button';
            if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
            if (tag === 'textarea' || el.isContentEditable) return 'textbox';
            if (tag === 'input') return { checkbox: 'checkbox', radio: 'radio', range: 'slider', search: 'searchbox', submit: 'button', button: 'button', reset: 'button', image: 'button', file: 'button' }[type] || 'textbox';
            return 'generic';
          };
          const name = el => {
            const labelled = el.getAttribute('aria-labelledby');
            const byIds = labelled ? clean(labelled.split(/\s+/).map(id => document.getElementById(id)?.innerText).join(' ')) : '';
            return clean(el.getAttribute('aria-label')) || byIds || clean([...(el.labels || [])].map(l => l.innerText).join(' '))
              || clean(el.innerText) || clean(el.querySelector?.('img[alt]')?.getAttribute('alt')) || clean(el.getAttribute('title'))
              || clean(el.getAttribute('placeholder')) || (el.type === 'submit' || el.type === 'button' ? clean(el.value) : '');
          };
          const state = el => {
            const s = [];
            if (el.checked || el.getAttribute('aria-checked') === 'true') s.push('checked');
            if (el.disabled || el.getAttribute('aria-disabled') === 'true') s.push('disabled');
            const expanded = el.getAttribute('aria-expanded'); if (expanded) s.push(expanded === 'true' ? 'expanded' : 'collapsed');
            if (el.getAttribute('aria-selected') === 'true') s.push('selected');
            if (el.required) s.push('required');
            if (document.activeElement === el) s.push('focused');
            return s.join(' ');
          };
          const value = el => {
            if (el.tagName === 'SELECT') return clean(el.selectedOptions?.[0]?.text);
            if (!('value' in el) || el.tagName === 'BUTTON' || ['checkbox', 'radio', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return '';
            return el.type === 'password' ? (el.value ? '••••' : '') : String(el.value).slice(0, 160);
          };
          // A focusable span inside a button is the same control; keep the outermost.
          const all = [...document.querySelectorAll(selector)].filter(shown)
            .filter(el => !el.parentElement?.closest(selector) || el.matches('a[href],button,input,textarea,select'));
          const ordered = [...all.filter(inView), ...all.filter(el => !inView(el))].slice(0, __ELEMENT_LIMIT__);
          const elements = ordered.map((el, i) => {
            const ref = String(i + 1); el.setAttribute('data-oinzi-ref', ref);
            const item = { ref, tag: el.tagName.toLowerCase(), role: role(el), text: name(el).slice(0, 160), href: el.getAttribute('href') };
            const v = value(el); if (v) item.value = v;
            const s = state(el); if (s) item.state = s;
            if (!inView(el)) item.offscreen = true;
            return item;
          });
          const headings = [...document.querySelectorAll('h1,h2,h3')].filter(shown).slice(0, 30).map(h => h.tagName[1] + ' ' + clean(h.innerText).slice(0, 120));
          return { title: document.title, url: location.href, text: (document.body?.innerText || '').slice(0, __TEXT_LIMIT__), elements, headings,
            scroll: { y: Math.round(scrollY), height: document.documentElement.scrollHeight, viewport: innerHeight } };
        })()
        """;

    private async Task<object> SnapshotAsync(BrowserTab tab, int textLimit, int elementLimit, bool diff)
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
        var url = main?["url"]?.GetValue<string>() ?? "";
        var combinedText = string.Join("\n", results.Select(r => $"[{r.Frame}] {r.Data["text"]}"));
        var text = combinedText[..Math.Min(textLimit, combinedText.Length)];
        var elements = results.SelectMany(r => (r.Data["elements"]?.AsArray() ?? []).Select(e =>
        {
            var item = e!.DeepClone().AsObject();
            item["ref"] = $"@{r.Frame}:{e["ref"]}";
            return item;
        })).Take(elementLimit).ToArray();
        // Refs are renumbered on every snapshot, so changes are tracked by what an element is, not by its ref.
        // Focus moves with every action, so it is left out or each diff would report the previously focused control.
        static string Key(JsonObject e) => string.Join("|", e["ref"]!.GetValue<string>().Split(':')[0], e["role"], e["text"], e["href"], e["value"],
            e["state"]?.GetValue<string>().Replace("focused", "").Trim());
        var keys = elements.Select(Key).ToHashSet();
        var textHash = text.GetHashCode();
        var previous = diff && tab.LastSnapshotUrl == url ? tab.LastSnapshotKeys : null;
        var textUnchanged = previous != null && tab.LastSnapshotText == textHash;
        var changed = previous == null ? elements : elements.Where(e => !previous.Contains(Key(e))).ToArray();
        var removed = previous?.Count(k => !keys.Contains(k)) ?? 0;
        tab.LastSnapshotKeys = keys;
        tab.LastSnapshotText = textHash;
        tab.LastSnapshotUrl = url;
        return new
        {
            title = main?["title"]?.GetValue<string>() ?? "",
            url,
            text = textUnchanged ? null : text,
            elements = changed,
            diff = previous == null ? null : new { textUnchanged, unchanged = elements.Length - changed.Length, removed },
            headings = main?["headings"]?.DeepClone(),
            scroll = main?["scroll"]?.DeepClone(),
            frames = results.Select(r => new { frame = r.Frame, url = r.Data["url"]?.GetValue<string>() }).ToArray(),
            dialogs = DrainDialogs(tab)
        };
    }

    private async Task<object> ActAsync(BrowserTab tab, string command, string target, string value)
    {
        if (target.Length == 0) throw new InvalidOperationException("Target required");
        var frameId = FrameIdOf(target);
        var selector = SelectorOf(target);
        await ShowActivityAsync(tab, command, selector, frameId);
        // Only a visible click needs a brief lead-in before navigation can replace the page.
        if (command == "click" && Active() == tab) await Task.Delay(90);
        try
        {
            // input tells the agent whether the page saw trusted events or the script fallback.
            switch (command)
            {
                case "click": return new { ok = true, input = await ClickAsync(tab, target) };
                case "fill": return new { ok = true, input = await FillAsync(tab, target, value) };
                case "hover": await HoverAsync(tab, target); return new { ok = true };
                default: return new { ok = true, selected = await SelectOptionAsync(tab, target, value) };
            }
        }
        finally { await ShowActivityAsync(tab, command, selector, frameId, done: true); }
    }

    private static async Task<bool> WaitAsync(BrowserTab tab, string selector, int timeout)
    {
        if (selector.Length == 0) throw new InvalidOperationException("CSS selector or text=... required");
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(timeout, 100, 30000));
        do
        {
            try
            {
                var probe = selector.StartsWith("text=")
                    ? $"(document.body?.innerText || '').toLowerCase().includes({JsonSerializer.Serialize(selector[5..].Trim().ToLowerInvariant())})"
                    : $"Boolean(document.querySelector({JsonSerializer.Serialize(selector)}))";
                if (await tab.View.CoreWebView2!.ExecuteScriptAsync(probe) == "true") return true;
            }
            catch (InvalidOperationException) { /* Navigation in progress */ }
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Timed out waiting for {selector}");
    }
}
