using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace PluckWindowsNative;

// Browser profiles (separate cookie jars, like Chrome profiles) and a per-profile vault of site logins.
// Agents see which profile holds which site account, never a password: `login` fills the form inside pluck, and only on
// the credential's own site in the credential's own profile, so a page cannot trick an agent into typing it elsewhere.
public sealed partial class BrowserForm
{
    private sealed class BrowserProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Color { get; set; } = "";
        // What the profile is for, written for the AI as much as for the person ("fn12 회사 계정, 네이버 블로그 운영").
        public string Note { get; set; } = "";
        // The other browser's profile it was imported from ("chrome:Profile 3"), used to open that profile's password export.
        public string Source { get; set; } = "";
    }

    private sealed class Credential
    {
        public string Id { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Site { get; set; } = "";
        public string Username { get; set; } = "";
        // DPAPI (current Windows user) protected, base64.
        public string Secret { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private sealed class ProfileBook
    {
        public List<BrowserProfile> Items { get; set; } = [];
        public List<Credential> Credentials { get; set; } = [];
        // WebView2 keeps a profile's files open while the browser runs, so their folders are removed at the next start.
        public List<string> PendingDelete { get; set; } = [];
    }

    private const string DefaultProfile = "default";
    private static readonly string[] ProfileColors = ["#7462ee", "#d97757", "#3f6ff0", "#2f9e6e", "#c2417a", "#c99a1a", "#5b8a9a", "#8a6d5b"];
    private ProfileBook profileBook = new();
    // Each profile is a workspace: the sidebar lists only its tabs, and new tabs open in it.
    // It follows the person's active tab, so selecting a tab from another profile switches workspace too.
    private string userProfile = DefaultProfile;
    private readonly Dictionary<string, int> lastWorkspaceTab = [];
    private string ProfilesFile => Path.Combine(dataDir, "profiles.json");
    private static string ProfileFolderName(string id) => "p-" + id;

    private void LoadProfiles()
    {
        try { if (File.Exists(ProfilesFile)) profileBook = JsonSerializer.Deserialize<ProfileBook>(File.ReadAllText(ProfilesFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { profileBook = new(); }
        if (!profileBook.Items.Any(p => p.Id == DefaultProfile))
            profileBook.Items.Insert(0, new BrowserProfile { Id = DefaultProfile, Name = "기본", Color = ProfileColors[0] });
        LinkBrowserProfiles();
        foreach (var folder in profileBook.PendingDelete.ToArray())
        {
            try
            {
                var path = Path.Combine(dataDir, "profile", "EBWebView", folder);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                profileBook.PendingDelete.Remove(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(ProfilesFile + ".tmp", JsonSerializer.Serialize(profileBook));
            File.Move(ProfilesFile + ".tmp", ProfilesFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private BrowserProfile? FindProfile(string idOrName) =>
        profileBook.Items.FirstOrDefault(p => p.Id == idOrName)
        ?? profileBook.Items.FirstOrDefault(p => string.Equals(p.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase));

    private string ProfileName(string id) => FindProfile(id)?.Name ?? "기본";

    private bool InWorkspace(BrowserTab tab) => tab.Agent || tab.Profile == userProfile;

    private void FollowWorkspace()
    {
        if (Active() is not { Agent: false } tab) return;
        lastWorkspaceTab[tab.Profile] = tab.Id;
        if (tab.Profile == userProfile) return;
        userProfile = tab.Profile;
        PostProfiles();
    }

    // Shows the tab last used in the profile, or opens one so the workspace is never empty.
    private void SwitchWorkspace(string profile)
    {
        userProfile = profile;
        var tab = lastWorkspaceTab.TryGetValue(profile, out var last) && browserTabs.GetValueOrDefault(last) is { Agent: false } known && known.Profile == profile
            ? known : OrderedTabs().FirstOrDefault(other => !other.Agent && other.Profile == profile);
        if (tab != null) ShowTab(tab);
        else _ = CreateTabAsync(HomeUrl, "user", true, profile: profile);
    }

    private CoreWebView2ControllerOptions? ControllerOptions(string profile)
    {
        if (profile == DefaultProfile || environment == null) return null;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileFolderName(profile);
        return options;
    }

    // DPAPI ties the vault to this Windows user. The extra entropy only keeps generic DPAPI dumps from reading it:
    // a program running as the same user (including an AI agent allowed to run commands) can still decrypt it.
    private static readonly byte[] VaultEntropy = Encoding.UTF8.GetBytes("pluck-site-vault-v1");

    private static string Protect(string text) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), VaultEntropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string secret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), VaultEntropy, DataProtectionScope.CurrentUser));

    // Hosts under which anyone can register a subdomain; a login saved for one would match strangers' sites.
    private static readonly HashSet<string> SharedSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.kr", "or.kr", "ne.kr", "go.kr", "ac.kr", "re.kr", "pe.kr", "kr", "com", "net", "org", "io", "co", "app", "dev",
        "co.jp", "ne.jp", "or.jp", "com.cn", "co.uk", "com.au", "github.io", "gitlab.io", "blogspot.com", "azurewebsites.net",
        "herokuapp.com", "vercel.app", "netlify.app", "pages.dev", "workers.dev", "web.app", "firebaseapp.com", "appspot.com",
        "cloudfront.net", "s3.amazonaws.com", "glitch.me", "repl.co", "ngrok.io", "ngrok-free.app", "onrender.com", "fly.dev"
    };

    // "https://nid.naver.com/nidlogin.login" and "naver.com" both become a bare host; subdomains of it match too.
    private static string SiteHost(string site)
    {
        var value = site.Trim().ToLowerInvariant();
        if (!value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) return "";
        var host = uri.IdnHost.TrimEnd('.');
        if (host.StartsWith("www.")) host = host[4..];
        return SharedSuffixes.Contains(host) || !host.Contains('.') && host != "localhost" ? "" : host;
    }

    private static bool HostMatches(string host, string site) =>
        site.Length > 0 && (host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase));

    // What agents may know: profiles and the accounts saved in each, without secrets.
    private object ProfilesForAgent() => profileBook.Items.Select(p => new
    {
        id = p.Id, name = p.Name, note = p.Note, isDefault = p.Id == DefaultProfile,
        openTabs = browserTabs.Values.Count(tab => tab.Profile == p.Id),
        logins = profileBook.Credentials.Where(c => c.Profile == p.Id)
            .Select(c => new { credential = c.Id, site = c.Site, username = c.Username, note = c.Note }).ToArray()
    }).ToArray();

    private object ProfilesMessage() => new
    {
        type = "profiles", current = userProfile,
        items = profileBook.Items.Select(p => new
        {
            id = p.Id, name = p.Name, color = p.Color, note = p.Note, isDefault = p.Id == DefaultProfile, source = SourceBrowser(p)?.Label,
            tabs = browserTabs.Values.Count(tab => tab.Profile == p.Id),
            logins = profileBook.Credentials.Where(c => c.Profile == p.Id)
                .Select(c => new { id = c.Id, site = c.Site, username = c.Username, note = c.Note, hasPassword = c.Secret.Length > 0 }).ToArray()
        }).ToArray()
    };

    private void PostProfiles() { PostChrome(ProfilesMessage()); RefreshSidebar(); }

    private void HandleProfileMessage(string type, JsonElement message)
    {
        var id = Read(message, "id");
        string Field(string name, int limit) => Clip(Read(message, name).Trim(), limit);
        switch (type)
        {
            case "prof.list": break;
            case "prof.use" when FindProfile(id) is { } used: SwitchWorkspace(used.Id); break;
            case "prof.newTab" when FindProfile(id) is { } target:
                userProfile = target.Id;
                PostChrome(new { type = "settings.close" });
                _ = CreateTabAsync(HomeUrl, "user", true, profile: target.Id);
                break;
            case "prof.add":
                var added = new BrowserProfile
                {
                    Id = "p" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                    Name = Field("name", 40) is { Length: > 0 } name ? name : "새 프로필",
                    Color = ProfileColors[profileBook.Items.Count % ProfileColors.Length], Note = Field("note", 300)
                };
                profileBook.Items.Add(added);
                break;
            case "prof.update" when FindProfile(id) is { } edited:
                if (Field("name", 40) is { Length: > 0 } newName) edited.Name = newName;
                if (message.TryGetProperty("note", out _)) edited.Note = Field("note", 300);
                if (Read(message, "color") is { Length: 7 } color && color.StartsWith('#')) edited.Color = color;
                break;
            case "prof.remove" when FindProfile(id) is { Id: not DefaultProfile } removed:
                if (browserTabs.Values.Any(tab => tab.Profile == removed.Id && tab.PendingCommands > 0))
                {
                    PostChrome(new { type = "settings.note", text = "이 프로필에서 AI가 작업 중입니다. 끝난 뒤 삭제할 수 있습니다." });
                    return;
                }
                foreach (var tab in browserTabs.Values.Where(tab => tab.Profile == removed.Id).ToArray()) CloseTab(tab);
                profileBook.Items.Remove(removed);
                profileBook.Credentials.RemoveAll(c => c.Profile == removed.Id);
                profileBook.PendingDelete.Add(ProfileFolderName(removed.Id));
                foreach (var session in sessions.Values.Where(session => session.Profile == removed.Id)) session.Profile = DefaultProfile;
                foreach (var chat in conversations.Where(chat => chat.Profile == removed.Id)) chat.Profile = null;
                if (userProfile == removed.Id) SwitchWorkspace(DefaultProfile);
                break;
            case "cred.add" when FindProfile(Read(message, "profile")) is { } owner:
            case "cred.update" when profileBook.Credentials.Any(c => c.Id == id):
                var site = SiteHost(Read(message, "site"));
                if (site.Length == 0) { PostChrome(new { type = "settings.note", text = "사이트 주소를 확인하세요. 예: naver.com (co.kr, github.io처럼 누구나 하위 주소를 만들 수 있는 주소는 쓸 수 없습니다)" }); return; }
                var credential = type == "cred.add"
                    ? new Credential { Id = "c" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Profile = FindProfile(Read(message, "profile"))!.Id }
                    : profileBook.Credentials.First(c => c.Id == id);
                // Pointing a saved password at another site needs the password again.
                if (type == "cred.update" && credential.Site != site && Read(message, "password").Length == 0) credential.Secret = "";
                credential.Site = site;
                credential.Username = Field("username", 200);
                credential.Note = Field("note", 200);
                // An empty password on edit keeps the saved one; the page never receives saved passwords.
                if (Read(message, "password") is { Length: > 0 } password) credential.Secret = Protect(password);
                if (type == "cred.add") profileBook.Credentials.Add(credential);
                break;
            case "cred.remove": profileBook.Credentials.RemoveAll(c => c.Id == id); break;
            case "cred.import" when FindProfile(id) is { } importTo: ImportPasswordCsv(importTo); return;
            default: return;
        }
        SaveProfiles();
        PostProfiles();
    }

    // A Chrome/Edge "Export passwords" file (name,url,username,password[,note]). It is read here and never reaches the chrome page.
    private void ImportPasswordCsv(BrowserProfile target)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"「{target.Name}」 프로필로 가져올 비밀번호 CSV 고르기",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        void Note(string text) => PostChrome(new { type = "settings.note", text });
        List<string[]> rows;
        try { rows = ParseCsv(File.ReadAllText(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note("파일을 읽지 못했습니다: " + ex.Message); return; }
        var header = rows.FirstOrDefault()?.Select(name => name.Trim().ToLowerInvariant()).ToArray() ?? [];
        int url = Array.IndexOf(header, "url"), user = Array.IndexOf(header, "username"), pass = Array.IndexOf(header, "password"), memo = Array.IndexOf(header, "note");
        if (url < 0 || pass < 0) { Note("브라우저에서 내보낸 비밀번호 CSV가 아닙니다. url, username, password 열이 있어야 합니다."); return; }
        int added = 0, updated = 0, skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            string Cell(int index) => index >= 0 && index < row.Length ? row[index] : "";
            // Android app entries and sites a login cannot be pinned to (co.kr, github.io...) are left out.
            var site = SiteHost(Cell(url));
            var password = Cell(pass);
            if (site.Length == 0 || password.Length == 0) { skipped++; continue; }
            var username = Clip(Cell(user).Trim(), 200);
            if (profileBook.Credentials.FirstOrDefault(c => c.Profile == target.Id && c.Site == site && c.Username == username) is { } existing)
            {
                existing.Secret = Protect(password);
                updated++;
                continue;
            }
            profileBook.Credentials.Add(new Credential
            {
                Id = "c" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Profile = target.Id,
                Site = site, Username = username, Secret = Protect(password), Note = Clip(Cell(memo).Trim(), 200)
            });
            added++;
        }
        SaveProfiles();
        PostProfiles();
        Note($"「{target.Name}」에 {added}개를 추가하고 {updated}개의 비밀번호를 바꿨습니다." + (skipped > 0 ? $" {skipped}개는 건너뛰었습니다(앱 전용 항목, 비밀번호 없음, 공용 주소)." : ""));
        if (MessageBox.Show(this, "가져온 CSV 파일에는 비밀번호가 암호화되지 않은 채 들어 있습니다." + Environment.NewLine + "이 파일을 지금 삭제할까요?" + Environment.NewLine + Environment.NewLine + dialog.FileName,
                "CSV 파일 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            try { File.Delete(dialog.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note("CSV 파일을 지우지 못했습니다. 직접 삭제하세요: " + dialog.FileName); }
        }
    }

    // RFC 4180: quoted cells may hold commas, doubled quotes and line breaks.
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        void EndRow()
        {
            row.Add(cell.ToString());
            cell.Clear();
            if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]);
            row.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
            }
            else cell.Append(c);
        }
        EndRow();
        return rows;
    }

    // Runs in a CDP isolated world: the page shares the DOM but cannot replace the functions this script calls
    // (JSON, querySelectorAll, focus...), so it cannot fake what is found. Nothing secret is ever put into a script.
    private const string LoginFindScript = """
        (() => {
          const usable = el => { const r = el.getBoundingClientRect(), s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && !el.disabled && !el.readOnly; };
          const inputs = [...document.querySelectorAll('input')].filter(usable);
          const textLike = el => ['text', 'email', 'tel', ''].includes(el.type) || el.autocomplete === 'username';
          const pass = inputs.find(el => el.type === 'password') || null;
          const named = el => /user|mail|login|id|account|아이디|이메일/i.test([el.name, el.id, el.autocomplete, el.placeholder, el.getAttribute('aria-label')].join(' '));
          const user = (pass ? inputs.slice(0, inputs.indexOf(pass)).reverse().find(textLike) : inputs.filter(textLike).find(named)) || null;
          window.__pluckLogin = { user, pass };
          return { user: !!user, pass: !!pass };
        })()
        """;

    private static string LoginFocusScript(string field) => $$"""
        (() => { const el = window.__pluckLogin?.{{field}}; if (!el || !el.isConnected) return false; el.focus(); return document.activeElement === el; })()
        """;

    private static string LoginCheckScript(string field, int length) => $$"""
        (() => { const el = window.__pluckLogin?.{{field}}; return !!el && el.isConnected && document.activeElement === el && el.value.length === {{length}}; })()
        """;

    private static async Task<(string FrameId, string LoaderId)> MainFrameAsync(CoreWebView2 core)
    {
        var frame = JsonNode.Parse(await CdpAsync(core, "Page.getFrameTree", new { }))?["frameTree"]?["frame"];
        return (frame?["id"]?.ToString() ?? "", frame?["loaderId"]?.ToString() ?? "");
    }

    private static async Task<JsonNode?> EvaluateInWorldAsync(CoreWebView2 core, int context, string expression)
    {
        var reply = JsonNode.Parse(await CdpAsync(core, "Runtime.evaluate", new { expression, contextId = context, returnByValue = true }));
        if (reply?["exceptionDetails"] != null) throw new InvalidOperationException("The login form changed while filling. Nothing more was typed.");
        return reply?["result"]?["value"];
    }

    private async Task<object> LoginAsync(BrowserTab tab, string credentialId)
    {
        var credential = profileBook.Credentials.FirstOrDefault(c => c.Id == credentialId)
            ?? throw new InvalidOperationException("Unknown credential. Call profiles() for the saved logins.");
        if (credential.Profile != tab.Profile)
            throw new InvalidOperationException($"This login is saved in profile \"{ProfileName(credential.Profile)}\", but the tab uses \"{ProfileName(tab.Profile)}\". Open a tab with that profile.");
        if (tab.ScriptedDocument == tab.DocumentSerial)
            throw new InvalidOperationException("A script ran on this page before. Reload the login page (goto its URL), then call login without running js first.");
        var core = tab.View.CoreWebView2!;
        // The host comes from the browser, never from the page. Only the top-level page counts: a login form framed by
        // another site is refused, since that site controls where keystrokes land.
        string TopHost() => Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri.IdnHost : "";
        var host = TopHost();
        if (!HostMatches(host, credential.Site))
            throw new InvalidOperationException($"This tab is on {host}, but the login is for {credential.Site}. Nothing was filled.");
        var (frameId, loaderId) = await MainFrameAsync(core);
        var world = JsonNode.Parse(await CdpAsync(core, "Page.createIsolatedWorld", new { frameId, worldName = "pluck-login" }))?["executionContextId"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Could not inspect the page.");
        var found = await EvaluateInWorldAsync(core, world, LoginFindScript);
        var hasUser = found?["user"]?.GetValue<bool>() == true;
        var hasPass = found?["pass"]?.GetValue<bool>() == true;
        if (!hasUser && !hasPass)
            throw new InvalidOperationException("No visible login form on this page. Open the site's login page first (forms inside frames are not supported).");
        var filled = new List<string>();
        async Task TypeAsync(string field, string value)
        {
            // Same document, same site, and focus on the chosen field, checked right before the keystrokes go out.
            if ((await MainFrameAsync(core)).LoaderId != loaderId || !HostMatches(TopHost(), credential.Site)
                || (await EvaluateInWorldAsync(core, world, LoginFocusScript(field)))?.GetValue<bool>() != true)
                throw new InvalidOperationException("The page changed or moved focus while filling. Nothing more was typed; try again.");
            await PressKeyAsync(core, "Control+A");
            await CdpAsync(core, "Input.insertText", new { text = value });
            // Only the length is compared, so the value never travels inside a script. There is no script fallback.
            if ((await EvaluateInWorldAsync(core, world, LoginCheckScript(field, value.Length)))?.GetValue<bool>() != true)
                throw new InvalidOperationException($"The {(field == "pass" ? "password" : "username")} field did not take the input. Fill it by hand or ask the person.");
        }
        if (hasUser && credential.Username.Length > 0) { await TypeAsync("user", credential.Username); filled.Add("username"); }
        if (hasPass && credential.Secret.Length > 0)
        {
            string secret;
            try { secret = Unprotect(credential.Secret); }
            catch (CryptographicException) { throw new InvalidOperationException("The saved password cannot be read on this Windows account. Ask the person to save it again in pluck settings."); }
            // From now on this document holds a password: agent scripts are refused and snapshots mask it.
            tab.LoginDocument = tab.DocumentSerial;
            tab.LoginSecrets.Add(secret);
            await TypeAsync("pass", secret);
            filled.Add("password");
        }
        return new
        {
            ok = filled.Count > 0, filled,
            next = hasPass ? "Click the login button to submit (Enter may not submit in a background tab), then check the result."
                : "Only the username step was shown. Continue to the password step, then call login again."
        };
    }

    // A document pluck typed a password into must not hand it back: no scripts, no value-matching selectors, and
    // snapshots have the secret masked.
    private static void GuardLoginDocument(BrowserTab tab, string command, string target)
    {
        if (tab.LoginDocument != tab.DocumentSerial) return;
        if (command == "js") throw new InvalidOperationException("Scripts are disabled on a page where pluck filled a saved login. Navigate away first.");
        // A "show password" toggle would put the password on screen.
        if (command == "screenshot") throw new InvalidOperationException("Screenshots are disabled on a page where pluck filled a saved login. Submit the form or navigate away first.");
        if (target.Contains("[value", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Value selectors are disabled on a page where pluck filled a saved login.");
    }

    private static object MaskSecrets(BrowserTab tab, object result)
    {
        if (tab.LoginDocument != tab.DocumentSerial || tab.LoginSecrets.Count == 0) return result;
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in tab.LoginSecrets)
            if (secret.Length > 0) json = json.Replace(JsonSerializer.Serialize(secret)[1..^1], "••••••••");
        return JsonNode.Parse(json)!;
    }
}
