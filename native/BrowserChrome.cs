using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PluckWindowsNative;

// The browser chrome (sidebar, URL pill, command palette) is an HTML page rendered by its own WebView2.
// Page WebViews live in `viewport`, a native panel positioned over the hole the chrome page reports.
public sealed partial class BrowserForm
{
    private readonly WebView2 chromeView = new() { Dock = DockStyle.Fill };
    private readonly Panel viewport = new() { BackColor = Color.White };
    private readonly Stack<(string Url, string Profile)> closedTabs = new();
    private bool chromeReady;
    private bool statePending;
    private bool sidebarCollapsed;
    private bool sidebarTarget;
    private bool sidebarPreparing;
    private bool sidebarMotion;
    private bool paletteOpen;
    private int nextOrder = 1;

    private void BuildChrome()
    {
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        using (var icon = typeof(BrowserForm).Assembly.GetManifestResourceStream("pluck.ico"))
            if (icon != null) Icon = new Icon(icon);
        var dark = SystemUsesDarkTheme();
        BackColor = dark ? Color.FromArgb(19, 22, 20) : Color.FromArgb(236, 237, 231);
        viewport.Controls.Add(tabs);
        viewport.Bounds = new Rectangle(268, 8, 1000, 800);
        viewport.Resize += (_, _) => RoundViewport();
        chromeView.DefaultBackgroundColor = BackColor;
        chromeView.KeyDown += OnShortcut;
        Controls.Add(viewport);
        Controls.Add(chromeView);
        viewport.BringToFront();
        HandleCreated += (_, _) => ApplyWindowTheme(BackColor, dark ? Color.FromArgb(231, 236, 229) : Color.FromArgb(29, 34, 31), dark);
    }

    private async Task InitializeChromeAsync()
    {
        await chromeView.EnsureCoreWebView2Async(environment);
        var core = chromeView.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("PLUCK_CHROME_DEVTOOLS") == "1";
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        core.WebMessageReceived += (_, e) => HandleChromeMessage(e.WebMessageAsJson);
        ServeRecordings(core);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:", StringComparison.Ordinal)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        using var stream = typeof(BrowserForm).Assembly.GetManifestResourceStream("chrome.html")
            ?? throw new InvalidOperationException("Chrome UI resource is missing");
        using var reader = new StreamReader(stream);
        // A data: URL keeps the chrome page off the network and avoids NavigateToString's 2 MB cap.
        core.Navigate("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(await reader.ReadToEndAsync())));
    }

    private void HandleChromeMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;
        BrowserTab? Target() => browserTabs.GetValueOrDefault(int.TryParse(Read(message, "id"), out var id) ? id : -1);
        var type = Read(message, "type");
        // Account actions may open file dialogs or consoles, so they run after the WebView2 event returns.
        if (type.StartsWith("acct.", StringComparison.Ordinal)) { var copy = message.Clone(); BeginInvoke(() => HandleAccountMessage(type, copy)); return; }
        if (type.StartsWith("settings.", StringComparison.Ordinal)) { HandleSettingsMessage(type, message); return; }
        if (type.StartsWith("sched.", StringComparison.Ordinal)) { HandleScheduleMessage(type, message); return; }
        if (type.StartsWith("prof.", StringComparison.Ordinal) || type.StartsWith("cred.", StringComparison.Ordinal)) { HandleProfileMessage(type, message); return; }
        if (type.StartsWith("setup.", StringComparison.Ordinal)) { HandleSetupMessage(type, message); return; }
        switch (type)
        {
            case "ready":
                chromeReady = true; RefreshSidebar(); PostAssistantStatus(); LoadAssistantHistory();
                PostChrome(new { type = "settings.prefs", prefs = PrefsMessage() });
                PostAccountUsage("claude"); PostAccountUsage("codex");
                PostChrome(ProfilesMessage());
                break;
            case "ai.send": SubmitAssistant(Read(message, "engine"), Read(message, "text"), Read(message, "context") == "True", Read(message, "picked")); break;
            case "ai.pick": _ = PickElementAsync(Read(message, "engine")); break;
            case "ai.queueRemove": RemoveQueuedPrompt(Read(message, "engine"), Read(message, "id")); break;
            case "ai.queueRun": if (currentConversations.GetValueOrDefault(Read(message, "engine")) is { } queued) RunNextPrompt(queued); break;
            case "ai.setting": UpdateSetting(Read(message, "engine"), Read(message, "name"), Read(message, "value")); break;
            case "ai.pickFolder": PickFolder(Read(message, "engine")); break;
            case "ai.attach": AddImage(Read(message, "engine"), Read(message, "name"), Read(message, "data"), Read(message, "preview")); break;
            case "ai.removeImage": RemoveImage(Read(message, "engine"), Read(message, "id")); break;
            case "ai.handoff": HandoffConversation(Read(message, "source"), Read(message, "id"), Read(message, "engine")); break;
            case "ai.stop": if (currentConversations.GetValueOrDefault(Read(message, "engine")) is { } stopping) StopAssistant(stopping); break;
            case "ai.reset": SelectConversation(Read(message, "engine")); break;
            case "ai.select": SelectConversation(Read(message, "engine"), Read(message, "id")); break;
            case "ai.delete": DeleteConversation(Read(message, "engine"), Read(message, "id")); break;
            case "ai.follow": prefs.FollowAgent = followAssistant = Read(message, "enabled") == "True"; SavePrefs(); break;
            case "ai.view": aiViewEngine = Engines.Contains(Read(message, "engine")) ? Read(message, "engine") : null; break;
            case "ai.focus": chromeView.Focus(); break;
            case "ai.blur": if (!paletteOpen) Active()?.View.Focus(); break;
            case "layout": PlaceViewport(message); break;
            case "theme":
                try { ApplyWindowTheme(ColorTranslator.FromHtml(Read(message, "canvas")), ColorTranslator.FromHtml(Read(message, "ink")), Read(message, "dark") == "True"); }
                catch (Exception ex) when (ex is ArgumentException or FormatException) { }
                break;
            case "select": if (Target() is { } selected) ShowTab(selected); break;
            case "shortcut":
                if (Enum.TryParse<Keys>(Read(message, "key"), out var key))
                    HandleShortcut(key | (Read(message, "ctrl") == "True" ? Keys.Control : 0)
                        | (Read(message, "shift") == "True" ? Keys.Shift : 0) | (Read(message, "alt") == "True" ? Keys.Alt : 0));
                break;
            case "close": if (Target() is { } closing) CloseTab(closing); break;
            case "open":
                var url = Read(message, "url");
                if (Read(message, "newTab") == "True") _ = CreateTabAsync(url, "user", true);
                else if (Active()?.View.CoreWebView2 is { } core) core.Navigate(NormalizeUrl(url));
                else _ = CreateTabAsync(url, "user", true);
                break;
            case "back": GoBack(); break;
            case "forward": GoForward(); break;
            case "reload": Active()?.View.Reload(); break;
            case "collapse": _ = AnimateSidebarAsync(Read(message, "collapsed") == "True"); break;
            case "sidebar.ready":
                sidebarPreparing = false;
                sidebarMotion = true;
                viewport.Visible = false;
                sidebarCollapsed = sidebarTarget;
                PostChrome(new { type = "sidebar.animate", collapsed = sidebarCollapsed });
                break;
            case "sidebar.done":
                sidebarMotion = false;
                PlaceViewport(message);
                if (!paletteOpen) viewport.Visible = true;
                PostChrome(new { type = "sidebar.restored" });
                RefreshSidebar();
                break;
            case "reorder":
                var order = 0;
                foreach (var item in message.GetProperty("ids").EnumerateArray())
                    if (browserTabs.GetValueOrDefault(item.GetInt32()) is { } tab) tab.Order = ++order;
                RefreshSidebar();
                break;
            case "palette": _ = OpenPaletteAsync(Read(message, "mode")); break;
            case "overview": _ = OpenOverviewAsync(); break;
            case "overview.refresh": _ = RefreshOverviewAsync(); break;
            case "overview.open": OpenSessionTab(Read(message, "space")); break;
            case "overview.stop": StopSession(Read(message, "space")); break;
            case "overview.takeover": StopSession(Read(message, "space")); OpenSessionTab(Read(message, "space")); break;
            case "rec.toggle":
                // From the AI panel the session is the conversation on screen; from the overview it is named directly.
                if (currentConversations.GetValueOrDefault(Read(message, "engine")) is { } recorded)
                {
                    OpenSession(SpaceOf(recorded), SessionTitle(recorded), "panel").Kind = "panel";
                    ToggleRecording(SpaceOf(recorded));
                }
                else ToggleRecording(Read(message, "space"));
                break;
            case "rec.list": PostRecordingList(); break;
            case "rec.open": PostRecording(Read(message, "id")); break;
            case "rec.video": _ = EncodeAndPostAsync(Read(message, "id"), reveal: true); break;
            case "rec.delete": DeleteRecording(Read(message, "id")); break;
            case "rec.export.begin": BeginExport(Read(message, "title")); break;
            case "rec.export.chunk": ExportChunk(Read(message, "data")); break;
            case "rec.export.end": EndExport(); break;
            case "frozen":
                viewport.Visible = false;
                // Refocusing a WebView2 that already has focus blurs its page for a moment, and that blur closes the palette.
                if (!chromeView.ContainsFocus) chromeView.Focus();
                PostChrome(new { type = "focus" });
                break;
            case "thaw":
                paletteOpen = false;
                viewport.Visible = true;
                Active()?.View.Focus();
                break;
        }
    }

    private void PostChrome(object message)
    {
        if (chromeReady && chromeView.CoreWebView2 != null)
            chromeView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private BrowserTab[] OrderedTabs() => browserTabs.Values.OrderBy(tab => tab.Agent)
        .ThenBy(tab => sessions.GetValueOrDefault(tab.Space)?.Order ?? int.MaxValue).ThenBy(tab => tab.Space, StringComparer.Ordinal)
        .ThenBy(tab => tab.Order).ThenBy(tab => tab.Id).ToArray();

    private object ChromeState()
    {
        var active = Active();
        return new
        {
            type = "state",
            active = active?.Id,
            canBack = active?.View.CanGoBack == true,
            canForward = active?.View.CanGoForward == true,
            url = active?.View.Source?.ToString() ?? "",
            loading = active?.Loading == true,
            collapsed = sidebarCollapsed,
            userProfile,
            profiles = profileBook.Items.Select(p => new { id = p.Id, name = p.Name, color = p.Color }).ToArray(),
            recording = recordings.Keys.ToArray(),
            tabs = OrderedTabs().Where(InWorkspace).Select(tab => new
            {
                id = tab.Id, space = tab.Space, label = tab.Agent ? SessionLabel(tab.Space) : "", agent = tab.Agent,
                title = tab.View.CoreWebView2?.DocumentTitle ?? "",
                url = tab.View.Source?.ToString() ?? "",
                favicon = tab.View.CoreWebView2?.FaviconUri ?? "",
                loading = tab.Loading, busy = tab.PendingCommands > 0, profile = tab.Profile,
                sessionProfile = tab.Agent ? sessions.GetValueOrDefault(tab.Space)?.Profile : null
            }).ToArray()
        };
    }

    // Coalesces bursts of tab events into a single state message per UI turn.
    private void RefreshSidebar()
    {
        if (!chromeReady || statePending || IsDisposed) return;
        statePending = true;
        BeginInvoke(() =>
        {
            statePending = false;
            if (!IsDisposed) PostChrome(ChromeState());
        });
    }

    private void RefreshNavigation() => RefreshSidebar();

    private async Task AnimateSidebarAsync(bool collapsed)
    {
        sidebarTarget = collapsed;
        if (sidebarMotion)
        {
            sidebarCollapsed = collapsed;
            PostChrome(new { type = "sidebar.animate", collapsed });
            return;
        }
        if (sidebarPreparing) return;
        if (paletteOpen || Active()?.View.CoreWebView2 == null)
        {
            sidebarCollapsed = collapsed;
            RefreshSidebar();
            return;
        }
        sidebarPreparing = true;
        string? shot = null;
        try
        {
            using var image = new MemoryStream();
            await Active()!.View.CoreWebView2!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, image);
            shot = "data:image/jpeg;base64," + Convert.ToBase64String(image.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
        if (shot == null)
        {
            sidebarPreparing = false;
            sidebarCollapsed = sidebarTarget;
            PostChrome(new { type = "sidebar.instant", collapsed = sidebarCollapsed });
            return;
        }
        PostChrome(new { type = "sidebar.prepare", shot });
    }

    private async Task OpenPaletteAsync(string mode)
    {
        if (!chromeReady) return;
        while (sidebarPreparing || sidebarMotion) await Task.Delay(16);
        var url = Active()?.View.Source?.ToString() ?? "";
        if (paletteOpen)
        {
            PostChrome(new { type = "palette", mode, url });
            return;
        }
        paletteOpen = true;
        string? shot = null;
        if (viewport.Visible && Active()?.View.CoreWebView2 is { } core)
        {
            try
            {
                using var image = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, image);
                shot = "data:image/jpeg;base64," + Convert.ToBase64String(image.ToArray());
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
        }
        PostChrome(new { type = "palette", mode, url, shot });
    }

    private void PlaceViewport(JsonElement message)
    {
        if (sidebarMotion) return;
        int Value(string name) => message.GetProperty(name).GetInt32();
        viewport.Tag = Value("r");
        var bounds = new Rectangle(Value("x"), Value("y"), Math.Max(1, Value("w")), Math.Max(1, Value("h")));
        if (viewport.Bounds == bounds) return;
        viewport.Bounds = bounds;
        // Let Windows coalesce paints; synchronous Update() stalls WebView input during layout.
    }

    private void RoundViewport()
    {
        var radius = viewport.Tag as int? ?? 10;
        using var path = new GraphicsPath();
        var (w, h, d) = (viewport.Width, viewport.Height, radius * 2);
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        var old = viewport.Region;
        viewport.Region = new Region(path);
        old?.Dispose();
    }

    private void ApplyWindowTheme(Color canvas, Color ink, bool dark)
    {
        BackColor = canvas;
        chromeView.DefaultBackgroundColor = canvas;
        if (!IsHandleCreated) return;
        var darkMode = dark ? 1 : 0;
        var caption = ColorTranslator.ToWin32(canvas);
        var text = ColorTranslator.ToWin32(ink);
        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(Handle, 34, ref caption, sizeof(int));  // DWMWA_BORDER_COLOR
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Retain native tab selection and WebView lifetime; the tab strip itself is drawn by the chrome page.
    private sealed class ContentTabs : TabControl
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x1328 && !DesignMode) { message.Result = IntPtr.Zero; return; } // TCM_ADJUSTRECT
            base.WndProc(ref message);
        }
    }
}
