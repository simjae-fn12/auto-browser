using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace PluckWindowsNative;

// Moving in from another browser: Chrome/Edge profiles become pluck profiles, and pluck can be the default browser.
// Only profile names and account emails are read from the other browser. Its cookies and passwords stay encrypted where
// they are; passwords come over through the browser's own "Export passwords" CSV (ImportPasswordCsv).
public sealed partial class BrowserForm
{
    private sealed record OtherBrowser(string Key, string Label, string DataDir, string Exe, string PasswordsUrl);

    private static readonly OtherBrowser[] OtherBrowsers =
    [
        new("chrome", "Chrome", @"Google\Chrome\User Data", "chrome.exe", "chrome://password-manager/settings"),
        new("edge", "Edge", @"Microsoft\Edge\User Data", "msedge.exe", "edge://settings/passwords")
    ];

    private sealed record FoundProfile(OtherBrowser Browser, string Folder, string Name, string Email, string Gaia)
    {
        public string Source => Browser.Key + ":" + Folder;
    }

    private static List<FoundProfile> FindBrowserProfiles()
    {
        var found = new List<FoundProfile>();
        foreach (var browser in OtherBrowsers)
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), browser.DataDir, "Local State");
            try
            {
                if (!File.Exists(file)) continue;
                // The browser keeps Local State open while it runs.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (JsonNode.Parse(stream)?["profile"]?["info_cache"] is not JsonObject cache) continue;
                foreach (var (folder, info) in cache)
                {
                    string Text(string name) => info?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
                    found.Add(new FoundProfile(browser, folder, Text("name"), Text("user_name"), Text("gaia_name")));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        // "Default", "Profile 1", "Profile 2"... in the order the browser created them.
        return found.OrderBy(p => Array.IndexOf(OtherBrowsers, p.Browser))
            .ThenBy(p => p.Folder == "Default" ? 0 : int.TryParse(p.Folder.Replace("Profile ", ""), out var n) ? n + 1 : int.MaxValue).ToList();
    }

    // An account already brought in from either browser counts as imported, so the same Google account in Chrome and Edge
    // does not become two workspaces. Profiles imported before Source existed are recognised by the email in their note.
    private BrowserProfile? ImportedFrom(FoundProfile found) =>
        profileBook.Items.FirstOrDefault(p => p.Source == found.Source)
        ?? (found.Email.Length > 0 ? profileBook.Items.FirstOrDefault(p => p.Note.Contains(found.Email, StringComparison.OrdinalIgnoreCase)) : null);

    // Profiles imported before Source existed get it back, so their 「비밀번호 내보내기」 opens the right browser profile.
    private void LinkBrowserProfiles()
    {
        foreach (var found in FindBrowserProfiles())
            if (ImportedFrom(found) is { Source.Length: 0, Id: not DefaultProfile } known) known.Source = found.Source;
    }

    private int ImportBrowserProfiles()
    {
        var added = 0;
        var taken = profileBook.Items.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var found in FindBrowserProfiles())
        {
            if (ImportedFrom(found) != null) continue;
            var account = found.Email.Split('@')[0];
            var name = Clip(found.Name.Length > 0 ? found.Name : account.Length > 0 ? account : found.Browser.Label + " " + found.Folder, 30);
            if (taken.Contains(name)) name = Clip($"{name} ({(account.Length > 0 ? account : found.Browser.Label)})", 36);
            var unique = name;
            for (var i = 2; taken.Contains(unique); i++) unique = $"{name} {i}";
            taken.Add(unique);
            var note = found.Email.Length == 0 ? $"{found.Browser.Label} 프로필 「{found.Name}」에서 가져옴"
                : $"{(found.Browser.Key == "chrome" ? "구글" : "계정")}: {found.Email}{(found.Gaia.Length > 0 ? $" ({found.Gaia})" : "")}";
            profileBook.Items.Add(new BrowserProfile
            {
                Id = "p" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Name = unique,
                Color = ProfileColors[profileBook.Items.Count % ProfileColors.Length], Note = Clip(note, 300), Source = found.Source
            });
            added++;
        }
        return added;
    }

    private static OtherBrowser? SourceBrowser(BrowserProfile profile) =>
        profile.Source.IndexOf(':') is > 0 and var split ? OtherBrowsers.FirstOrDefault(b => b.Key == profile.Source[..split]) : null;

    private static string? BrowserExe(OtherBrowser browser)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + browser.Exe);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    // Opens the export page in the very browser profile this pluck profile came from; the person exports there and then
    // brings the file in with 「CSV 가져오기」. pluck itself never opens the other browser's password store.
    private void OpenPasswordExport(BrowserProfile profile)
    {
        void Note(string text) => PostChrome(new { type = "settings.note", text });
        if (SourceBrowser(profile) is not { } browser) { Note("다른 브라우저에서 가져온 프로필이 아닙니다."); return; }
        if (BrowserExe(browser) is not { } exe) { Note($"{browser.Label}을(를) 찾지 못했습니다."); return; }
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("--profile-directory=" + profile.Source[(profile.Source.IndexOf(':') + 1)..]);
        start.ArgumentList.Add(browser.PasswordsUrl);
        try { Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Note($"{browser.Label}을(를) 열지 못했습니다: " + ex.Message); return; }
        Note($"{browser.Label}에서 「비밀번호 내보내기」로 CSV를 받은 뒤, 이 프로필의 「CSV 가져오기」로 넣으세요.");
    }

    private const string BrowserKey = @"Software\Clients\StartMenuInternet\pluck";
    private const string UrlProgId = "pluckURL";
    private const string HtmlProgId = "pluckHTML";

    private static bool IsDefaultBrowser()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        return key?.GetValue("ProgId") as string == UrlProgId;
    }

    // Windows lets only the person choose the default browser, so pluck registers itself as a candidate (per user,
    // no admin rights) and opens the Settings page where pluck can be picked.
    private static void RegisterBrowser()
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var icon = $"\"{exe}\",0";
        void ProgId(string id, string name)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + id);
            key.SetValue("", name);
            using (var defaultIcon = key.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\" \"%1\"");
            using var app = key.CreateSubKey("Application");
            app.SetValue("ApplicationName", "pluck");
            app.SetValue("AppUserModelId", "pluck.browser");
        }
        ProgId(UrlProgId, "pluck URL");
        ProgId(HtmlProgId, "pluck HTML 문서");
        using (var browser = Registry.CurrentUser.CreateSubKey(BrowserKey))
        {
            browser.SetValue("", "pluck");
            using (var defaultIcon = browser.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var command = browser.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\"");
            using var capabilities = browser.CreateSubKey("Capabilities");
            capabilities.SetValue("ApplicationName", "pluck");
            capabilities.SetValue("ApplicationDescription", "AI와 함께 쓰는 브라우저");
            capabilities.SetValue("ApplicationIcon", icon);
            using (var menu = capabilities.CreateSubKey("StartMenu")) menu.SetValue("StartMenuInternet", "pluck");
            using (var urls = capabilities.CreateSubKey("URLAssociations")) { urls.SetValue("http", UrlProgId); urls.SetValue("https", UrlProgId); }
            using var files = capabilities.CreateSubKey("FileAssociations");
            foreach (var extension in new[] { ".htm", ".html", ".xhtml" }) files.SetValue(extension, HtmlProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) registered.SetValue("pluck", BrowserKey + @"\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static void UnregisterBrowser()
    {
        try
        {
            foreach (var key in new[] { BrowserKey, $@"Software\Classes\{UrlProgId}", $@"Software\Classes\{HtmlProgId}" })
                Registry.CurrentUser.DeleteSubKeyTree(key, false);
            using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue("pluck", false);
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) run?.DeleteValue("pluck", false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    // A moved pluck.exe would leave links pointing at a file that no longer exists; a test build elsewhere must not take over.
    private static void RefreshBrowserRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{UrlProgId}\shell\open\command");
        if (key?.GetValue("") is string command && command.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } exe && !File.Exists(exe))
            RegisterBrowser();
    }

    private void MakeDefaultBrowser()
    {
        try { RegisterBrowser(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            PostChrome(new { type = "settings.note", text = "pluck을 브라우저로 등록하지 못했습니다: " + ex.Message });
            return;
        }
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=pluck") { UseShellExecute = true });
        PostChrome(new { type = "settings.note", text = "Windows 설정에서 「기본값으로 설정」을 누르세요. pluck으로 돌아오면 상태가 바뀝니다." });
    }

    private void PostSetupState()
    {
        var found = FindBrowserProfiles();
        // The same account signed in to both browsers becomes one profile, as ImportBrowserProfiles does it.
        var fresh = found.Where(p => ImportedFrom(p) == null).DistinctBy(p => p.Email.Length > 0 ? p.Email.ToLowerInvariant() : p.Source).ToArray();
        PostChrome(new
        {
            type = "setup.state", isDefault = IsDefaultBrowser(),
            tools = new { claude = FindTool("claude") != null, codex = FindTool("codex") != null, node = FindNode() != null },
            browsers = OtherBrowsers.Select(b => new { label = b.Label, count = found.Count(p => p.Browser == b), fresh = fresh.Count(p => p.Browser == b) })
                .Where(b => b.count > 0).ToArray()
        });
    }

    private void HandleSetupMessage(string type, JsonElement message)
    {
        switch (type)
        {
            case "setup.state": break;
            case "setup.default": MakeDefaultBrowser(); break;
            case "setup.install": InstallTool(Read(message, "tool")); break;
            case "setup.import":
                var added = ImportBrowserProfiles();
                SaveProfiles();
                PostProfiles();
                PostChrome(new { type = "settings.note", text = added > 0 ? $"프로필 {added}개를 만들었습니다. 로그인 상태는 옮겨 오지 않으니 프로필마다 pluck에서 한 번씩 로그인하세요." : "새로 가져올 프로필이 없습니다." });
                break;
            case "setup.exportPasswords" when FindProfile(Read(message, "id")) is { } profile: OpenPasswordExport(profile); break;
            default: return;
        }
        PostSetupState();
    }

    // Shown once on a new install, only when there is something to do.
    private async Task OfferSetupAsync()
    {
        for (var i = 0; i < 200 && !chromeReady; i++) await Task.Delay(50);
        prefs.SetupOffered = true;
        SavePrefs();
        if (IsDefaultBrowser() && FindBrowserProfiles().All(p => ImportedFrom(p) != null) && FindTool("claude") != null && FindTool("codex") != null) return;
        await Task.Delay(600);
        await OpenSettingsAsync("start");
    }

    // Node runs terminal agents (agent.mjs, mcp.mjs) and npm installs Codex.
    private static string? FindNode() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"))
            .Select(dir => { try { return Path.Combine(dir.Trim().Trim('"'), "node.exe"); } catch (ArgumentException) { return ""; } })
            .FirstOrDefault(File.Exists);

    // Each tool's official install command, run in a visible PowerShell window so the person sees progress, prompts and
    // errors. pluck re-checks when its window is active again.
    private const string NodeInstall = "winget install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements; "
        + "$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); ";
    private static readonly Dictionary<string, (string Name, string Script)> ToolInstallers = new()
    {
        ["claude"] = ("Claude Code", "irm https://claude.ai/install.ps1 | iex; "),
        ["codex"] = ("Codex", "if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { " + NodeInstall + "} npm install -g @openai/codex; "),
        ["node"] = ("Node.js", NodeInstall)
    };

    private void InstallTool(string tool)
    {
        if (!ToolInstallers.TryGetValue(tool, out var installer)) return;
        var script = $"$Host.UI.RawUI.WindowTitle = '{installer.Name} 설치'; Write-Host '{installer.Name}을(를) 설치합니다.'; " + installer.Script
            + "Write-Host ''; Read-Host '끝났습니다. Enter를 누르면 창이 닫힙니다. pluck으로 돌아가면 설치 상태가 바뀝니다'";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) start.ArgumentList.Add(arg);
        try { Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex) { PostChrome(new { type = "settings.note", text = "설치 창을 열지 못했습니다: " + ex.Message }); return; }
        PostChrome(new { type = "settings.note", text = $"{installer.Name} 설치 창을 열었습니다. 끝나면 「AI 계정」에서 로그인하세요." });
    }

    internal static string DataDirectory { get; } = Environment.GetEnvironmentVariable("PLUCK_NATIVE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pluck-windows-native");

    // One pluck per data folder: a link clicked in another app starts a second pluck.exe, which hands the link to the
    // running one through this pipe and exits.
    internal static string InstanceName { get; } =
        "pluck-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataDirectory).ToLowerInvariant())))[..16];

    internal static List<string> LaunchLinks { get; } = [];
    private bool linksReady;

    internal static string[] LinksFromArgs(IEnumerable<string> args) => args
        .Select(arg => arg.Trim().Trim('"'))
        .Select(arg => Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" ? uri.AbsoluteUri
            : File.Exists(arg) ? new Uri(Path.GetFullPath(arg)).AbsoluteUri : null)
        .OfType<string>().Take(20).ToArray();

    internal static bool HandOff(string[] links)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            // The click gave this process the foreground; the running pluck may take it.
            AllowSetForegroundWindow(-1);
            using var writer = new StreamWriter(pipe);
            writer.Write(JsonSerializer.Serialize(links));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private async void ListenForLinks()
    {
        while (!IsDisposed)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe);
                OpenLinks(JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync()) ?? []);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }

    private void OpenLinks(string[] received)
    {
        var links = LinksFromArgs(received);
        if (!linksReady) { LaunchLinks.AddRange(links); return; }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (links.Length > 0 && paletteOpen) PostChrome(new { type = "settings.close" });
        foreach (var link in links) _ = CreateTabAsync(link, "user", true, profile: userProfile);
    }

    private async Task OpenLaunchLinksAsync()
    {
        foreach (var link in LaunchLinks) await CreateTabAsync(link, "user", true, profile: userProfile);
        LaunchLinks.Clear();
        linksReady = true;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
