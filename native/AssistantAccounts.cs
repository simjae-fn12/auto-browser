using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OinziBrowser;

// Several Claude/Codex logins side by side. Each extra account is its own CLI config folder (CLAUDE_CONFIG_DIR / CODEX_HOME),
// so every login refreshes its tokens in place; copying credential files around breaks because refresh tokens rotate.
// "default" is the person's normal ~/.claude or ~/.codex login and is never moved.
public sealed partial class BrowserForm
{
    private sealed class Account
    {
        public string Id { get; set; } = "";
        public string Engine { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? Plan { get; set; }
        public bool? LoggedIn { get; set; }
        public double? Five { get; set; }
        public double? Week { get; set; }
        public DateTime? UsageAt { get; set; }
        // Why the last login failed, shown next to the account until the next attempt.
        [System.Text.Json.Serialization.JsonIgnore] public string? LoginError { get; set; }
    }

    private sealed class AccountBook
    {
        public Dictionary<string, string> Active { get; set; } = [];
        public List<Account> Items { get; set; } = [];
    }

    private const string DefaultAccount = "default";
    private AccountBook accounts = new();
    private bool accountsLoaded;
    private readonly HashSet<string> accountChecks = [];
    // Login consoles still open, by engine/id; kept apart from status checks so neither clears the other.
    private readonly Dictionary<string, LoginRun> accountLogins = [];
    private string AccountsFile => Path.Combine(dataDir, "accounts.json");

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Where the person's own login lives, honouring a config folder they set for their terminal.
    private static string DefaultConfigDir(string engine) => engine == "claude"
        ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claude ? claude : Path.Combine(UserHome, ".claude")
        : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex ? codex : Path.Combine(UserHome, ".codex");

    private string AccountDir(string engine, string id) => Path.Combine(dataDir, "accounts", engine, id);

    private void LoadAccounts()
    {
        if (accountsLoaded) return;
        accountsLoaded = true;
        try { if (File.Exists(AccountsFile)) accounts = JsonSerializer.Deserialize<AccountBook>(File.ReadAllText(AccountsFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { accounts = new(); }
        foreach (var engine in Engines)
            if (!accounts.Items.Any(a => a.Engine == engine && a.Id == DefaultAccount))
                accounts.Items.Insert(0, new Account { Id = DefaultAccount, Engine = engine, Name = "기본 계정" });
        accounts.Items.RemoveAll(a => a.Id != DefaultAccount && !Directory.Exists(AccountDir(a.Engine, a.Id)));
    }

    private void SaveAccounts()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(AccountsFile + ".tmp", JsonSerializer.Serialize(accounts));
            File.Move(AccountsFile + ".tmp", AccountsFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Account? FindAccount(string engine, string id) => accounts.Items.FirstOrDefault(a => a.Engine == engine && a.Id == id);

    private string ActiveAccountId(string engine)
    {
        LoadAccounts();
        return accounts.Active.GetValueOrDefault(engine) is { } id && FindAccount(engine, id) != null ? id : DefaultAccount;
    }

    // Points a CLI process at the active account. Returns the account id stored on the conversation, null for the default login.
    private string? ApplyAccount(ProcessStartInfo info, string engine)
    {
        var id = ActiveAccountId(engine);
        if (id == DefaultAccount) return null;
        var dir = AccountDir(engine, id);
        Directory.CreateDirectory(dir);
        var busy = aiRuns.Values.Any(run => run.Chat.Engine == engine && run.Chat.SessionAccount == id);
        try { ShareConfig(engine, dir, shareMcp: !busy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        info.Environment[engine == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"] = dir;
        return id;
    }

    // Extra accounts see the same settings, skills and MCP servers as the default login. Only the login itself differs.
    // Files are copied one way from the person's folder; nothing in their own config is ever changed.
    private static void ShareConfig(string engine, string dir, bool shareMcp = true)
    {
        var source = DefaultConfigDir(engine);
        string[] files = engine == "claude" ? ["settings.json", "CLAUDE.md"] : ["config.toml", "AGENTS.md"];
        string[] folders = engine == "claude" ? ["skills", "agents", "commands"] : ["skills", "prompts"];
        foreach (var name in files) CopyIfNewer(Path.Combine(source, name), Path.Combine(dir, name));
        foreach (var name in folders) LinkFolder(Path.Combine(source, name), Path.Combine(dir, name));
        if (engine != "claude") return;
        // Plugin manifests hold absolute paths that Claude rewrites from its own config folder. A shared manifest would let an
        // extra account point the person's plugins into its folder, so manifests are copied and only the content folders linked.
        var plugins = Path.Combine(source, "plugins");
        if (Directory.Exists(plugins))
        {
            var mine = Path.Combine(dir, "plugins");
            if (new DirectoryInfo(mine) is { Exists: true } existing && existing.Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(mine);
            Directory.CreateDirectory(mine);
            foreach (var file in Directory.EnumerateFiles(plugins, "*.json")) CopyIfNewer(file, Path.Combine(mine, Path.GetFileName(file)));
            foreach (var folder in Directory.EnumerateDirectories(plugins)) LinkFolder(folder, Path.Combine(mine, Path.GetFileName(folder)));
        }
        if (shareMcp) ShareMcpServers(dir);
    }

    private static void CopyIfNewer(string from, string to)
    {
        if (File.Exists(from) && (!File.Exists(to) || File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to))) File.Copy(from, to, true);
    }

    private static void LinkFolder(string from, string to)
    {
        if (Directory.Exists(from) && !Directory.Exists(to)) Junction(to, from);
    }

    // User-scoped MCP servers sit in ~/.claude.json, next to per-login state, so only that key is carried over.
    private static void ShareMcpServers(string dir)
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
            ? Path.Combine(custom, ".claude.json") : Path.Combine(UserHome, ".claude.json");
        if (!File.Exists(home)) return;
        var servers = JsonNode.Parse(File.ReadAllText(home))?["mcpServers"];
        if (servers == null) return;
        var target = Path.Combine(dir, ".claude.json");
        var config = File.Exists(target) ? JsonNode.Parse(File.ReadAllText(target)) as JsonObject ?? [] : [];
        if (JsonNode.DeepEquals(config["mcpServers"], servers)) return;
        config["mcpServers"] = servers.DeepClone();
        File.WriteAllText(target + ".tmp", config.ToJsonString());
        File.Move(target + ".tmp", target, true);
    }

    // A directory junction needs no admin rights or developer mode, unlike a symbolic link.
    private static void Junction(string link, string target)
    {
        if (link.Contains('"') || target.Contains('"')) return;
        var info = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info);
        if (process == null) return;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        process.WaitForExit(5000);
    }

    private object AccountsMessage() => new
    {
        type = "accounts",
        engines = Engines.ToDictionary(engine => engine, engine => new
        {
            active = ActiveAccountId(engine),
            items = accounts.Items.Where(a => a.Engine == engine).Select(a => new
            {
                id = a.Id, name = a.Name, email = a.Email ?? "", plan = a.Plan ?? "", loggedIn = a.LoggedIn,
                checking = accountChecks.Contains(engine + "/" + a.Id), loggingIn = accountLogins.ContainsKey(engine + "/" + a.Id),
                loginCode = accountLogins.GetValueOrDefault(engine + "/" + a.Id)?.Code, isDefault = a.Id == DefaultAccount, loginError = a.LoginError,
                five = a.Five, week = a.Week, usageAt = a.UsageAt,
                running = aiRuns.Values.Count(run => run.Chat.Engine == engine && (run.Chat.SessionAccount ?? DefaultAccount) == a.Id),
                path = a.Id == DefaultAccount ? DefaultConfigDir(engine) : AccountDir(engine, a.Id)
            }).ToArray()
        })
    };

    private void PostAccounts()
    {
        PostChrome(AccountsMessage());
        foreach (var engine in Engines) PostAccountUsage(engine);
    }

    private void HandleAccountMessage(string type, JsonElement message)
    {
        LoadAccounts();
        var engine = Read(message, "engine");
        if (type != "acct.list" && !Engines.Contains(engine)) return;
        var id = Read(message, "id");
        switch (type)
        {
            case "acct.list":
                PostAccounts();
                foreach (var account in accounts.Items.Where(a => a.LoggedIn == null)) _ = CheckAccountAsync(account);
                return;
            case "acct.refresh":
                foreach (var account in accounts.Items.Where(a => a.Engine == engine)) _ = CheckAccountAsync(account);
                break;
            case "acct.use" when FindAccount(engine, id) != null:
                accounts.Active[engine] = id;
                if (engine == "codex") codexModels = null;
                SaveAccounts();
                PostAccountUsage(engine);
                if (currentConversations.ContainsKey(engine)) PostSettings(engine);
                break;
            case "acct.add":
                var importing = Read(message, "import") == "True";
                var picked = importing ? PickCredentials(engine) : null;
                if (importing && picked == null) break;
                var added = new Account
                {
                    Id = "a" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Engine = engine,
                    Name = Clip(Read(message, "name").Trim(), 40) is { Length: > 0 } name ? name : "새 계정", LoggedIn = false
                };
                Directory.CreateDirectory(AccountDir(engine, added.Id));
                accounts.Items.Add(added);
                SaveAccounts();
                if (picked != null) ImportCredentials(added, picked);
                else StartLogin(added);
                break;
            case "acct.login" when FindAccount(engine, id) is { } login: StartLogin(login); break;
            case "acct.cancelLogin": CancelLogin(engine + "/" + id); break;
            case "acct.import" when FindAccount(engine, id) is { Id: not DefaultAccount } target:
                if (PickCredentials(engine) is { } file) ImportCredentials(target, file);
                break;
            case "acct.rename" when FindAccount(engine, id) is { } renamed && Clip(Read(message, "name").Trim(), 40) is { Length: > 0 } newName:
                renamed.Name = newName;
                SaveAccounts();
                break;
            case "acct.remove" when FindAccount(engine, id) is { Id: not DefaultAccount } removed:
                if (aiRuns.Values.Any(run => run.Chat.Engine == engine && run.Chat.SessionAccount == removed.Id)
                    || accountLogins.ContainsKey(engine + "/" + removed.Id) || accountChecks.Contains(engine + "/" + removed.Id))
                {
                    PostChrome(new { type = "acct.error", engine, text = "이 계정으로 작업 중이거나 로그인 창이 열려 있습니다. 끝난 뒤 삭제할 수 있습니다." });
                    return;
                }
                accounts.Items.Remove(removed);
                if (accounts.Active.GetValueOrDefault(engine) == removed.Id) accounts.Active.Remove(engine);
                SaveAccounts();
                try { DeleteAccountDir(AccountDir(engine, removed.Id)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                break;
            case "acct.terminal" when FindAccount(engine, id) is { } shell:
                var variable = engine == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";
                Clipboard.SetText(shell.Id == DefaultAccount
                    ? $"Remove-Item Env:{variable} -ErrorAction SilentlyContinue; {engine}"
                    : $"$env:{variable} = '{AccountDir(engine, shell.Id)}'; {engine}");
                PostChrome(new { type = "acct.note", engine, text = "PowerShell 명령을 복사했습니다. 터미널에 붙여 넣으면 그 창에서만 이 계정을 씁니다." });
                return;
            default: return;
        }
        PostAccounts();
    }

    // The account folder holds junctions to the person's skills and plugins; remove the links, never what they point to.
    private static void DeleteAccountDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        UnlinkAll(new DirectoryInfo(dir));
        Directory.Delete(dir, true);
    }

    private static void UnlinkAll(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { if (entry is DirectoryInfo) Directory.Delete(entry.FullName); else entry.Delete(); }
            else if (entry is DirectoryInfo child) UnlinkAll(child);
    }

    private sealed class LoginRun(Process process)
    {
        public Process Process { get; } = process;
        public string? Code { get; set; }
        public int? TabId { get; set; }
        public bool Answered { get; set; }
        public bool Cancelled { get; set; }
        public Queue<string> Output { get; } = new();
    }

    private static readonly System.Text.RegularExpressions.Regex LoginUrl = new(@"https://[^\s\x1b\x07]+");
    private static readonly System.Text.RegularExpressions.Regex DeviceCode = new(@"\b[A-Z0-9]{4}-[A-Z0-9]{4,6}\b");
    private const string ClaudeCodeCallback = "https://platform.claude.com/oauth/code/callback";

    // The login page opens in a Oinzi tab instead of the system browser. Claude without a console falls back to its
    // paste-the-code flow, so Oinzi reads the code from the callback page's address and types it in. Codex uses device
    // authorization (a one-time code), the only flow that does not launch the system browser on its own.
    private void StartLogin(Account account)
    {
        var key = account.Engine + "/" + account.Id;
        if (accountLogins.TryGetValue(key, out var open) && !open.Process.HasExited)
        {
            if (open.TabId is { } tabId && browserTabs.GetValueOrDefault(tabId) is { } tab) { PostChrome(new { type = "settings.close" }); ShowTab(tab); }
            return;
        }
        if (FindTool(account.Engine) is not { } exe)
        {
            PostChrome(new { type = "acct.error", engine = account.Engine, text = $"{account.Engine} CLI를 찾을 수 없습니다." });
            return;
        }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = UserHome,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (account.Engine == "codex") AddCodexDaemonFlag(info);
        foreach (var arg in account.Engine == "claude" ? new[] { "auth", "login" } : ["login", "--device-auth"]) info.ArgumentList.Add(arg);
        IsolateAssistantEnvironment(info);
        info.Environment["BROWSER"] = "none";
        if (account.Id != DefaultAccount)
        {
            var dir = AccountDir(account.Engine, account.Id);
            Directory.CreateDirectory(dir);
            info.Environment[account.Engine == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"] = dir;
        }
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PostChrome(new { type = "acct.error", engine = account.Engine, text = "로그인을 시작하지 못했습니다: " + ex.Message });
            return;
        }
        if (process == null) return;
        var login = new LoginRun(process);
        accountLogins[key] = login;
        account.LoginError = null;
        LogSetup($"{account.Engine} login started ({account.Id}): {info.FileName} {string.Join(' ', info.ArgumentList)}");
        PostAccounts();
        void Watch(StreamReader reader) => _ = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (login.Output) { login.Output.Enqueue(line); if (login.Output.Count > 8) login.Output.Dequeue(); }
                OnUi(() => LogSetup($"{account.Engine} login: {line}"));
                OnUi(() => OnLoginOutput(account, login, line));
            }
        });
        Watch(process.StandardOutput);
        Watch(process.StandardError);
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            await Task.Delay(300);
            OnUi(() => FinishLogin(account, key, login));
        });
    }

    private void OnLoginOutput(Account account, LoginRun login, string line)
    {
        if (login.Process.HasExited && login.TabId == null) return;
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", "");
        // The code may come before or on the same line as the link; it is kept either way and announced once.
        if (account.Engine == "codex" && login.Code == null && DeviceCode.Match(line) is { Success: true } code)
        {
            login.Code = code.Value;
            try { Clipboard.SetText(code.Value); } catch (System.Runtime.InteropServices.ExternalException) { }
            PostAccounts();
            PostChrome(new { type = "toast", text = $"Codex 로그인 코드 {code.Value} — 클립보드에 복사했어요. 열린 탭에 붙여 넣으세요." });
            return;
        }
        if (login.TabId != null || LoginUrl.Match(line) is not { Success: true } url) return;
        login.TabId = -1;
        _ = OpenLoginTabAsync(account, login, url.Value.TrimEnd('.', ')'));
    }

    private async Task OpenLoginTabAsync(Account account, LoginRun login, string url)
    {
        PostChrome(new { type = "settings.close" });
        BrowserTab tab;
        try { tab = await CreateTabAsync(url, "user", true); }
        catch (InvalidOperationException) { return; }
        login.TabId = tab.Id;
        if (account.Engine != "claude") return;
        tab.View.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (login.Answered || !e.Uri.StartsWith(ClaudeCodeCallback, StringComparison.OrdinalIgnoreCase)) return;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(e.Uri).Query);
            if (query["code"] is not { Length: > 0 } code) return;
            // The page shows the same value for people to paste by hand: code#state.
            login.Answered = true;
            try { login.Process.StandardInput.WriteLine(code + (query["state"] is { Length: > 0 } state ? "#" + state : "")); login.Process.StandardInput.Flush(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        };
    }

    private void FinishLogin(Account account, string key, LoginRun login)
    {
        if (accountLogins.GetValueOrDefault(key) == login) accountLogins.Remove(key);
        var ok = login.Process.ExitCode == 0;
        string tail;
        lock (login.Output) tail = string.Join(" ", login.Output.Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", "").Trim()).Where(line => line.Length > 0).TakeLast(2));
        LogSetup($"{account.Engine} login ended: exit {login.Process.ExitCode}{(login.Cancelled ? " (cancelled)" : "")}");
        login.Process.Dispose();
        if (login.TabId is { } tabId && browserTabs.GetValueOrDefault(tabId) is { } tab && ok) CloseTab(tab);
        if (FindAccount(account.Engine, account.Id) != account) return;
        if (!ok && !login.Cancelled) account.LoginError = tail.Length > 0 ? Clip(tail, 200) : "로그인 과정이 오류로 끝났어요.";
        if (!login.Cancelled)
            PostChrome(new { type = "toast", text = ok ? $"{account.Name} 계정에 로그인했어요." : "로그인하지 못했어요. " + account.LoginError });
        _ = CheckAccountAsync(account);
    }

    private void CancelLogin(string key)
    {
        if (accountLogins.GetValueOrDefault(key) is not { } login) return;
        login.Cancelled = true;
        Kill(login.Process);
        if (login.TabId is { } tabId && browserTabs.GetValueOrDefault(tabId) is { } tab) CloseTab(tab);
    }

    private string? PickCredentials(string engine)
    {
        using var dialog = new OpenFileDialog
        {
            Title = engine == "claude" ? "Claude 자격 증명 파일 (.credentials.json) 고르기" : "Codex 자격 증명 파일 (auth.json) 고르기",
            Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private static string CredentialFile(string engine, string dir) => Path.Combine(dir, engine == "claude" ? ".credentials.json" : "auth.json");

    private static string? RefreshToken(JsonNode? node, string engine) =>
        (engine == "claude" ? node?["claudeAiOauth"]?["refreshToken"] : node?["tokens"]?["refresh_token"])?.ToString();

    // Brings in a saved credential file such as auth-simjae.json. A refresh token works in one place only: once either copy
    // refreshes, the other is signed out. So the live default login and tokens another account already holds are refused.
    private void ImportCredentials(Account account, string file)
    {
        var engine = account.Engine;
        string? Refuse(string text) { PostChrome(new { type = "acct.error", engine, text }); PostAccounts(); return null; }
        try
        {
            var full = Path.GetFullPath(file);
            if (string.Equals(full, Path.GetFullPath(CredentialFile(engine, DefaultConfigDir(engine))), StringComparison.OrdinalIgnoreCase))
            { Refuse("기본 계정이 지금 쓰는 로그인 파일입니다. 복사하면 둘 중 하나가 로그아웃되므로, 다른 계정은 '브라우저로 로그인'으로 추가하세요."); return; }
            var node = JsonNode.Parse(File.ReadAllText(full));
            var valid = engine == "claude" ? node?["claudeAiOauth"] != null : node?["tokens"] != null || node?["OPENAI_API_KEY"] is JsonValue;
            if (!valid) throw new JsonException();
            if (RefreshToken(node, engine) is { Length: > 0 } token)
                foreach (var other in accounts.Items.Where(a => a.Engine == engine && a != account))
                {
                    var path = CredentialFile(engine, other.Id == DefaultAccount ? DefaultConfigDir(engine) : AccountDir(engine, other.Id));
                    if (File.Exists(path) && RefreshToken(JsonNode.Parse(File.ReadAllText(path)), engine) == token)
                    { Refuse($"'{other.Name}' 계정과 같은 로그인입니다. 같은 토큰을 두 곳에서 쓰면 한쪽이 로그아웃됩니다."); return; }
                }
            var dir = AccountDir(engine, account.Id);
            Directory.CreateDirectory(dir);
            File.Copy(full, CredentialFile(engine, dir), true);
            if (account.Name == "새 계정") account.Name = Clip(Path.GetFileNameWithoutExtension(full), 40);
            SaveAccounts();
            _ = CheckAccountAsync(account);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Refuse(engine == "claude"
                ? "Claude 자격 증명 파일이 아닙니다. claudeAiOauth 항목이 있는 .credentials.json을 고르세요."
                : "Codex 자격 증명 파일이 아닙니다. tokens 항목이 있는 auth.json을 고르세요.");
        }
    }

    private async Task CheckAccountAsync(Account account)
    {
        var key = account.Engine + "/" + account.Id;
        if (!accountChecks.Add(key)) return;
        PostAccounts();
        try
        {
            var dir = account.Id == DefaultAccount ? null : AccountDir(account.Engine, account.Id);
            var (loggedIn, email, plan) = account.Engine == "claude"
                ? await Task.Run(() => ClaudeLoginStatus(dir))
                : CodexLoginStatus(dir ?? DefaultConfigDir("codex"));
            if (FindAccount(account.Engine, account.Id) != account) return;
            account.LoggedIn = loggedIn;
            // A signed-out account must not keep showing whoever was signed in there before.
            if (!loggedIn) { account.Email = null; account.Plan = null; }
            if (email != null) account.Email = email;
            if (plan != null) account.Plan = plan;
            if (loggedIn && account.Name == "새 계정" && email != null) account.Name = email;
            SaveAccounts();
        }
        finally
        {
            accountChecks.Remove(key);
            if (!IsDisposed) PostAccounts();
        }
    }

    private static (bool LoggedIn, string? Email, string? Plan) ClaudeLoginStatus(string? dir)
    {
        if (FindTool("claude") is not { } exe) return (false, null, null);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = UserHome
        };
        foreach (var arg in new[] { "auth", "status", "--json" }) info.ArgumentList.Add(arg);
        IsolateAssistantEnvironment(info);
        if (dir != null) info.Environment["CLAUDE_CONFIG_DIR"] = dir;
        try
        {
            using var process = Process.Start(info);
            if (process == null) return (false, null, null);
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { Kill(process); return (false, null, null); }
            var status = JsonNode.Parse(output.Result);
            return (status?["loggedIn"]?.GetValue<bool>() == true, status?["email"]?.ToString(), status?["subscriptionType"]?.ToString());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (false, null, null); }
    }

    // Codex keeps the signed-in identity in the id_token of auth.json; reading it avoids starting the CLI.
    private static (bool LoggedIn, string? Email, string? Plan) CodexLoginStatus(string dir)
    {
        try
        {
            var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "auth.json")));
            if (auth?["tokens"]?["id_token"]?.ToString() is { } token && token.Split('.') is { Length: >= 2 } parts)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return (true, claims?["email"]?.ToString(), claims?["https://api.openai.com/auth"]?["chatgpt_plan_type"]?.ToString());
            }
            return (auth?["OPENAI_API_KEY"]?.ToString() is { Length: > 0 }, null, "API 키");
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException or InvalidOperationException)
        { return (false, null, null); }
    }

    // Usage limits belong to an account, so each run's report is kept on the account it ran under.
    private void RecordAccountUsage(Conversation chat, JsonObject data)
    {
        if (FindAccount(chat.Engine, chat.SessionAccount ?? DefaultAccount) is not { } account) return;
        if (data["five"] is JsonValue five) account.Five = five.GetValue<double>();
        if (data["week"] is JsonValue week) account.Week = week.GetValue<double>();
        account.UsageAt = DateTime.UtcNow;
        SaveAccounts();
    }

    private void PostAccountUsage(string engine)
    {
        var account = FindAccount(engine, ActiveAccountId(engine));
        // The account name only matters in the panel once there is more than one to tell apart.
        var several = accounts.Items.Count(a => a.Engine == engine) > 1;
        PostChrome(new { type = "ai.accountUsage", engine, name = several ? account?.Name ?? "" : "", five = account?.Five, week = account?.Week });
    }
}
