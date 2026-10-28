using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OinziBrowser;

// Installs Claude Code and Codex inside Oinzi, with progress on screen: no terminal window, no Node.js, no admin rights.
// Claude follows the official install.ps1 step for step (latest version, manifest checksum, claude.exe, `claude install`).
// Codex uses the official standalone package attached to each openai/codex GitHub release (the layout npm ships).
// Every step and failure goes to logs\setup.log, so a problem on someone else's PC can be read afterwards.
public sealed partial class BrowserForm
{
    private const string ClaudeDownloads = "https://downloads.claude.ai/claude-code-releases";
    private static readonly HttpClient downloadClient = CreateDownloadClient();
    private readonly Dictionary<string, (string Text, double? Fraction)> toolInstalls = [];

    internal static string CodexInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Oinzi", "codex");

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Oinzi");
        return client;
    }

    private void LogSetup(string text)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "setup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private object ToolProgressMessage() => toolInstalls.ToDictionary(pair => pair.Key, pair => new { text = pair.Value.Text, fraction = pair.Value.Fraction });

    private async Task InstallToolAsync(string tool)
    {
        if (tool is not ("claude" or "codex") || toolInstalls.ContainsKey(tool)) return;
        var name = tool == "claude" ? "Claude Code" : "Codex";
        void Progress(string text, double? fraction)
        {
            toolInstalls[tool] = (text, fraction);
            PostChrome(new { type = "setup.progress", tool, state = "running", text, fraction });
        }
        Progress("준비하는 중…", null);
        LogSetup($"{name} install started");
        try
        {
            if (tool == "claude") await InstallClaudeAsync(Progress);
            else await InstallCodexAsync(Progress);
            toolInstalls.Remove(tool);
            LogSetup($"{name} installed: {FindTool(tool)}");
            PostChrome(new { type = "setup.progress", tool, state = "done", text = $"{name}를 설치했어요. 이제 로그인하세요." });
            LoadAccounts();
            foreach (var account in accounts.Items.Where(a => a.Engine == tool)) _ = CheckAccountAsync(account);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            toolInstalls.Remove(tool);
            LogSetup($"{name} install failed: {ex}");
            var reason = ex switch
            {
                InvalidDataException => ex.Message,
                HttpRequestException or TaskCanceledException => "다운로드 서버에 연결하지 못했어요. 인터넷 연결이나 회사 방화벽을 확인하세요.",
                UnauthorizedAccessException => "설치 폴더에 쓸 권한이 없어요. 백신 프로그램이 막고 있는지 확인하세요.",
                IOException when tool == "codex" => "Codex가 실행 중이라 바꿀 수 없어요. Codex를 쓰는 창을 닫고 다시 시도하세요.",
                _ => ex.Message
            };
            PostChrome(new { type = "setup.progress", tool, state = "error", text = $"{name} 설치에 실패했어요. {reason}" });
        }
        PostSetupState();
        PostAssistantStatus();
        PostAccounts();
    }

    private static async Task DownloadAsync(string url, string file, Action<double?> progress)
    {
        using var response = await downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(file);
        var buffer = new byte[1 << 16];
        long done = 0;
        var shown = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (DateTime.UtcNow - shown < TimeSpan.FromMilliseconds(250)) continue;
            shown = DateTime.UtcNow;
            progress(total > 0 ? done / (double)total : null);
        }
        progress(1);
    }

    private static async Task<(int Code, string Output)> RunHiddenAsync(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = UserHome
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(exe + " did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { Kill(process); throw new InvalidDataException("설치가 5분 넘게 끝나지 않았어요."); }
        return (process.ExitCode, (await output + "\n" + await error).Trim());
    }

    private async Task InstallClaudeAsync(Action<string, double?> progress)
    {
        var platform = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win32-arm64" : "win32-x64";
        var version = (await downloadClient.GetStringAsync(ClaudeDownloads + "/latest")).Trim();
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+"))
            throw new InvalidDataException("Claude 다운로드 서버가 버전을 알려 주지 않았어요. 지원 국가가 아니거나 회사 네트워크가 막고 있을 수 있어요.");
        var manifest = JsonNode.Parse(await downloadClient.GetStringAsync($"{ClaudeDownloads}/{version}/manifest.json"));
        var checksum = manifest?["platforms"]?[platform]?["checksum"]?.ToString()
            ?? throw new InvalidDataException($"이 PC({platform})용 Claude Code를 찾지 못했어요.");
        var folder = Path.Combine(UserHome, ".claude", "downloads");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"claude-{version}-{platform}.exe");
        try
        {
            await DownloadAsync($"{ClaudeDownloads}/{version}/{platform}/claude.exe", file, f => progress($"Claude Code {version} 받는 중", f));
            progress("받은 파일 확인 중…", null);
            string actual;
            await using (var stream = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("받은 파일이 손상됐어요. 다시 시도하세요.");
            progress("설치하는 중…", null);
            var (code, output) = await RunHiddenAsync(file, "install");
            LogSetup($"claude install exit {code}: {output}");
            if (code != 0) throw new InvalidDataException("설치 프로그램이 오류로 끝났어요: " + Clip(output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim(), 160));
        }
        finally
        {
            await Task.Delay(500);
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // `claude install` puts the launcher in ~\.local\bin; a terminal only finds it there through PATH.
        await AddToUserPathAsync(Path.Combine(UserHome, ".local", "bin"));
        if (FindTool("claude") == null) throw new InvalidDataException("설치는 끝났는데 claude.exe를 찾지 못했어요.");
    }

    private async Task InstallCodexAsync(Action<string, double?> progress)
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        var asset = $"codex-package-{arch}-pc-windows-msvc.tar.gz";
        var release = JsonNode.Parse(await downloadClient.GetStringAsync("https://api.github.com/repos/openai/codex/releases/latest"));
        var version = (release?["tag_name"]?.ToString() ?? "").Replace("rust-v", "");
        var url = release?["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.ToString() == asset)?["browser_download_url"]?.ToString()
            ?? throw new InvalidDataException("이번 Codex 릴리스에 Windows 설치 파일이 없어요. 잠시 뒤 다시 시도하세요.");
        var archive = Path.Combine(Path.GetTempPath(), "oinzi-" + asset);
        var staging = CodexInstallDir + ".new";
        try
        {
            await DownloadAsync(url, archive, f => progress($"Codex {version} 받는 중", f));
            progress("압축 푸는 중…", null);
            await Task.Run(() =>
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                using var file = File.OpenRead(archive);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, staging, true);
            });
            if (!File.Exists(Path.Combine(staging, "bin", "codex.exe"))) throw new InvalidDataException("Codex 설치 파일의 구성이 예상과 달라요.");
            if (Directory.Exists(CodexInstallDir)) Directory.Delete(CodexInstallDir, true);
            Directory.Move(staging, CodexInstallDir);
        }
        finally
        {
            try { File.Delete(archive); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        await AddToUserPathAsync(Path.Combine(CodexInstallDir, "bin"));
        lock (codexNoDaemon) codexNoDaemon.Clear();
    }

    // Adds a folder to the user's PATH for new terminals, and to this process so Oinzi sees it at once.
    private static async Task AddToUserPathAsync(string folder)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!current.Split(';').Contains(folder, StringComparer.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("PATH", current.TrimEnd(';') + ";" + folder);
        await Task.Run(() =>
        {
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            if (!user.Split(';').Contains(folder, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("Path", (user.TrimEnd(';') + ";" + folder).TrimStart(';'), EnvironmentVariableTarget.User);
        });
    }
}
