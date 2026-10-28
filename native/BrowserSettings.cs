using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PluckWindowsNative;

// The settings page (Ctrl+,). Preferences live in settings.json; accounts have their own file (AssistantAccounts.cs).
// It opens over a frozen viewport like the overview, so `paletteOpen` covers it too.
public sealed partial class BrowserForm
{
    private sealed class Prefs
    {
        public string Startup { get; set; } = "restore";
        public string HomePage { get; set; } = "https://www.google.com/";
        public string Search { get; set; } = "google";
        public string Theme { get; set; } = "system";
        public string? DownloadDir { get; set; }
        public bool Autofill { get; set; } = true;
        public bool AgentOverlay { get; set; } = true;
        public bool FollowAgent { get; set; } = true;
        public int AgentIdleMinutes { get; set; } = 15;
        public bool AutoMp4 { get; set; } = true;
        public bool AttachTab { get; set; } = true;
        // A message sent while the AI works joins the running task (steer) or waits for it to finish (queue).
        public string FollowUp { get; set; } = "steer";
        public bool NotifyDone { get; set; } = true;
        public bool DoneSound { get; set; } = true;
        public string CustomInstructions { get; set; } = "";
        // The 「시작하기」 page (other browsers' profiles, default browser) opens by itself only on the first start.
        public bool SetupOffered { get; set; }
    }

    private static readonly Dictionary<string, string> SearchEngines = new()
    {
        ["google"] = "https://www.google.com/search?q=",
        ["naver"] = "https://search.naver.com/search.naver?query=",
        ["bing"] = "https://www.bing.com/search?q=",
        ["duckduckgo"] = "https://duckduckgo.com/?q="
    };
    private static readonly int[] IdleChoices = [0, 5, 10, 15, 30, 60, 120];
    private static Prefs prefs = new();
    private string PrefsFile => Path.Combine(dataDir, "settings.json");

    private void LoadPrefs()
    {
        try { if (File.Exists(PrefsFile)) prefs = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { prefs = new(); }
        followAssistant = prefs.FollowAgent;
    }

    private void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(PrefsFile + ".tmp", JsonSerializer.Serialize(prefs));
            File.Move(PrefsFile + ".tmp", PrefsFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string HomeUrl => Uri.TryCreate(prefs.HomePage, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : Home;
    private static string SearchUrl(string query) => SearchEngines.GetValueOrDefault(prefs.Search, SearchEngines["google"]) + Uri.EscapeDataString(query);

    // Profile-wide preferences; the chrome page shares the profile, so the theme reaches it through prefers-color-scheme.
    private void ApplyProfilePrefs()
    {
        if (chromeView.CoreWebView2?.Profile is { } chrome) ApplyProfile(chrome);
        foreach (var tab in browserTabs.Values) if (tab.View.CoreWebView2?.Profile is { } profile) ApplyProfile(profile);
    }

    private static void ApplyProfile(CoreWebView2Profile profile)
    {
        profile.PreferredColorScheme = prefs.Theme switch
        {
            "light" => CoreWebView2PreferredColorScheme.Light,
            "dark" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
        if (prefs.DownloadDir is { Length: > 0 } folder && Directory.Exists(folder)) profile.DefaultDownloadFolderPath = folder;
    }

    private void ApplyTabPrefs(CoreWebView2 core)
    {
        ApplyProfile(core.Profile);
        core.Settings.IsGeneralAutofillEnabled = prefs.Autofill;
        core.Settings.IsPasswordAutosaveEnabled = prefs.Autofill;
    }

    private async Task OpenSettingsAsync(string section)
    {
        if (!chromeReady) return;
        while (sidebarPreparing || sidebarMotion) await Task.Delay(16);
        string? shot = null;
        if (!paletteOpen && viewport.Visible && Active()?.View.CoreWebView2 is { } core)
        {
            var capture = CaptureAsync(core, CoreWebView2CapturePreviewImageFormat.Jpeg);
            if (await Task.WhenAny(capture, Task.Delay(CaptureWait)) == capture && await capture is { } bytes)
                shot = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        paletteOpen = true;
        LoadAccounts();
        PostChrome(new { type = "settings", shot, section, prefs = PrefsMessage(), info = SettingsInfo() });
        PostChrome(AccountsMessage());
        PostSchedules();
        PostSetupState();
        foreach (var account in accounts.Items.Where(a => a.LoggedIn == null)) _ = CheckAccountAsync(account);
    }

    private object PrefsMessage() => new
    {
        startup = prefs.Startup, homePage = prefs.HomePage, search = prefs.Search, theme = prefs.Theme, autofill = prefs.Autofill,
        agentOverlay = prefs.AgentOverlay, agentIdleMinutes = prefs.AgentIdleMinutes.ToString(), autoMp4 = prefs.AutoMp4, attachTab = prefs.AttachTab,
        followUp = prefs.FollowUp, notifyDone = prefs.NotifyDone, doneSound = prefs.DoneSound, customInstructions = prefs.CustomInstructions,
        followAgent = followAssistant, downloadDir = PrefsDownloadDir()
    };


    private object SettingsInfo() => new
    {
        version = typeof(BrowserForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "",
        runtime = environment?.BrowserVersionString ?? "",
        dataDir, filesDir = userFilesDir,
        mcp = Path.Combine(dataDir, "mcp.mjs"),
        claude = FindTool("claude") ?? "", codex = FindTool("codex") ?? ""
    };

    private void HandleSettingsMessage(string type, JsonElement message)
    {
        switch (type)
        {
            case "settings.open": _ = OpenSettingsAsync(Read(message, "section")); return;
            case "settings.set": UpdatePref(Read(message, "name"), Read(message, "value")); return;
            case "settings.pickDownload":
                BeginInvoke(() =>
                {
                    using var dialog = new FolderBrowserDialog { Description = "다운로드한 파일을 저장할 폴더", UseDescriptionForTitle = true, InitialDirectory = PrefsDownloadDir() };
                    if (dialog.ShowDialog(this) == DialogResult.OK) UpdatePref("downloadDir", dialog.SelectedPath);
                });
                return;
            case "settings.clear": _ = ClearBrowsingDataAsync(Read(message, "kinds")); return;
            case "settings.reveal":
                var path = Read(message, "what") switch { "data" => dataDir, "files" => userFilesDir, "downloads" => PrefsDownloadDir(), _ => null };
                if (path != null && Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            case "settings.copyMcp":
                Clipboard.SetText($"{(Read(message, "engine") == "codex" ? "codex" : "claude")} mcp add pluck -- node \"{Path.Combine(dataDir, "mcp.mjs")}\"");
                PostChrome(new { type = "settings.note", text = "등록 명령을 복사했습니다. 터미널에 붙여 넣어 실행하세요." });
                return;
        }
    }

    private string PrefsDownloadDir() => prefs.DownloadDir ?? chromeView.CoreWebView2?.Profile.DefaultDownloadFolderPath
        ?? Path.Combine(UserHome, "Downloads");

    private void UpdatePref(string name, string value)
    {
        value = value.Trim();
        switch (name)
        {
            case "startup" when value is "restore" or "home": prefs.Startup = value; break;
            case "homePage":
                // Free text would quietly become a search-results home page.
                if (value.Contains(' ') && !Uri.IsWellFormedUriString(value, UriKind.Absolute)) { PostChrome(new { type = "settings.note", text = "홈 페이지에는 주소를 입력하세요." }); return; }
                var url = NormalizeUrl(value.Length == 0 ? Home : value);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
                prefs.HomePage = url;
                break;
            case "search" when SearchEngines.ContainsKey(value): prefs.Search = value; break;
            case "theme" when value is "system" or "light" or "dark": prefs.Theme = value; ApplyProfilePrefs(); break;
            case "downloadDir" when Directory.Exists(value): prefs.DownloadDir = value; ApplyProfilePrefs(); break;
            case "autofill":
                prefs.Autofill = value == "True";
                foreach (var tab in browserTabs.Values) if (tab.View.CoreWebView2 is { } core) ApplyTabPrefs(core);
                break;
            case "agentOverlay": prefs.AgentOverlay = value == "True"; break;
            case "followAgent": prefs.FollowAgent = followAssistant = value == "True"; PostChrome(new { type = "ai.follow", enabled = followAssistant }); break;
            case "agentIdleMinutes" when int.TryParse(value, out var minutes) && IdleChoices.Contains(minutes): prefs.AgentIdleMinutes = minutes; break;
            case "autoMp4": prefs.AutoMp4 = value == "True"; break;
            case "attachTab": prefs.AttachTab = value == "True"; break;
            case "followUp" when value is "steer" or "queue": prefs.FollowUp = value; break;
            case "notifyDone": prefs.NotifyDone = value == "True"; break;
            case "doneSound": prefs.DoneSound = value == "True"; break;
            case "customInstructions": prefs.CustomInstructions = value.Length > 4000 ? value[..4000] : value; break;
            default: return;
        }
        SavePrefs();
        PostChrome(new { type = "settings.prefs", prefs = PrefsMessage() });
    }

    // A finished task is easy to miss while the person works in another window, so the taskbar button flashes until they come back.
    private void NotifyDone(Conversation chat, bool failed)
    {
        if (!prefs.NotifyDone || IsDisposed) return;
        if (prefs.DoneSound) (failed ? System.Media.SystemSounds.Hand : System.Media.SystemSounds.Asterisk).Play();
        if (GetForegroundWindow() == Handle) return;
        var info = new FlashInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(), Window = Handle, Flags = 3 | 12 }; // FLASHW_ALL | FLASHW_TIMERNOFG
        FlashWindowEx(ref info);
        ShowNotification((chat.Engine == "claude" ? "Claude" : "Codex") + (failed ? " 작업이 멈췄어요" : " 작업을 마쳤어요"), chat.Title, chat);
    }

    private NotifyIcon? notifyIcon;
    private Conversation? notifiedChat;

    // A tray balloon shows as a Windows notification; clicking it brings pluck back on that conversation.
    private void ShowNotification(string title, string text, Conversation? chat = null)
    {
        if (notifyIcon == null)
        {
            notifyIcon = new NotifyIcon { Icon = Icon, Text = "pluck", Visible = true };
            notifyIcon.BalloonTipClicked += (_, _) => RevealConversation(notifiedChat);
            notifyIcon.Click += (_, _) => RevealConversation(null);
            FormClosed += (_, _) => notifyIcon.Dispose();
        }
        notifiedChat = chat;
        notifyIcon.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(text) ? " " : Clip(text, 120), ToolTipIcon.None);
    }

    private void RevealConversation(Conversation? chat)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (chat == null || !conversations.Contains(chat)) return;
        if (currentConversations.GetValueOrDefault(chat.Engine) != chat) SelectConversation(chat.Engine, chat.Id);
        PostChrome(new { type = "assist.show", engine = chat.Engine });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private async Task ClearBrowsingDataAsync(string kinds)
    {
        if (chromeView.CoreWebView2?.Profile is not { } chromeProfile) return;
        var profiles = browserTabs.Values.Select(tab => tab.View.CoreWebView2?.Profile).OfType<CoreWebView2Profile>().Append(chromeProfile)
            .DistinctBy(profile => profile.ProfileName).ToArray();
        var selected = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CoreWebView2BrowsingDataKinds flags = 0;
        foreach (var kind in selected)
            flags |= kind switch
            {
                "history" => CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory,
                "cookies" => CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage,
                "cache" => CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage,
                "autofill" => CoreWebView2BrowsingDataKinds.GeneralAutofill | CoreWebView2BrowsingDataKinds.PasswordAutosave,
                _ => 0
            };
        if (flags == 0) return;
        try
        {
            foreach (var profile in profiles) await profile.ClearBrowsingDataAsync(flags);
            PostChrome(new { type = "settings.note", text = "선택한 데이터를 삭제했습니다." + (selected.Contains("cookies") ? " 사이트에서 로그아웃됐을 수 있습니다." : "") });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            PostChrome(new { type = "settings.note", text = "삭제하지 못했습니다: " + ex.Message });
        }
    }
}
