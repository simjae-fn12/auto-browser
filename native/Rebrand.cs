using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OinziBrowser;

// Oinzi was called pluck before 0.9. On the first start its folders move once to the new names, so logins, profiles,
// saved passwords and AI conversations carry over. The old AppData folder is left as a junction to the new one: paths
// saved outside Oinzi (an MCP server registered with `claude mcp add`, WebView2's own extension records) keep working.
internal static class Rebrand
{
    private const string OldData = "pluck-windows-native";
    private const string OldDocs = "pluck";

    // Returns false when the old pluck is still running and holds its files; Oinzi must not start on an empty folder.
    public static bool Migrate()
    {
        if ((Environment.GetEnvironmentVariable("OINZI_DATA_DIR") ?? Environment.GetEnvironmentVariable("PLUCK_NATIVE_DATA_DIR")) != null) return true;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var oldData = Path.Combine(appData, OldData);
        var newData = BrowserForm.DataDirectory;
        if (!Directory.Exists(newData) && Directory.Exists(oldData) && new DirectoryInfo(oldData).LinkTarget == null)
        {
            try { Directory.Move(oldData, newData); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            Junction(oldData, newData);
            Follow(newData, (oldData, newData));
            MoveRegistration();
        }
        // The documents folder moves on its own: a program working inside it (a terminal, an editor) blocks the move,
        // and Oinzi then starts with empty folders. It is tried again on every start until it succeeds.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var oldDocs = Path.Combine(documents, OldDocs);
        var newDocs = Path.Combine(documents, "Oinzi");
        if (Directory.Exists(oldDocs) && Directory.Exists(newData) && (!Directory.Exists(newDocs) || !Directory.EnumerateFiles(newDocs, "*", SearchOption.AllDirectories).Any()))
        {
            try
            {
                if (Directory.Exists(newDocs)) Directory.Delete(newDocs, true);
                Directory.Move(oldDocs, newDocs);
                Follow(newData, (oldDocs, newDocs));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }

    private static void Follow(string data, (string From, string To) move)
    {
        RewriteSavedPaths(data, [move]);
        RenameClaudeProjects(data, [move]);
        // The built-in skills were written under their old names and would load twice.
        var assistant = Path.Combine(data, "assistant");
        foreach (var root in (Directory.Exists(assistant) ? Directory.GetDirectories(assistant) : []).Append(Path.Combine(move.To, "AI 작업")))
        {
            var skills = Path.Combine(root, ".claude", "skills");
            if (!Directory.Exists(skills)) continue;
            foreach (var stale in Directory.GetDirectories(skills, "pluck-*"))
                try { Directory.Delete(stale, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Junction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false });
            process?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // Settings, conversations and accounts store absolute paths as JSON strings.
    private static void RewriteSavedPaths(string data, List<(string From, string To)> moves)
    {
        foreach (var file in Directory.GetFiles(data, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var updated = text;
                foreach (var (from, to) in moves)
                {
                    string Escaped(string path) => JsonSerializer.Serialize(path)[1..^1];
                    updated = updated.Replace(Escaped(from) + "\\\\", Escaped(to) + "\\\\").Replace(Escaped(from) + "\"", Escaped(to) + "\"");
                }
                if (updated != text) File.WriteAllText(file, updated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Claude Code files conversations under projects\<working folder with every other character turned into '-'>, so a
    // conversation only resumes from the folder it started in. Its folders follow the move, in every Claude account.
    private static void RenameClaudeProjects(string data, List<(string From, string To)> moves)
    {
        static string Encode(string path) => Regex.Replace(path, "[^A-Za-z0-9]", "-");
        var configs = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") };
        var accounts = Path.Combine(data, "accounts", "claude");
        if (Directory.Exists(accounts)) configs.AddRange(Directory.GetDirectories(accounts));
        foreach (var config in configs)
        {
            var projects = Path.Combine(config, "projects");
            if (!Directory.Exists(projects)) continue;
            foreach (var project in Directory.GetDirectories(projects))
            {
                var name = Path.GetFileName(project);
                foreach (var (from, to) in moves)
                {
                    var prefix = Encode(from);
                    if (name != prefix && !name.StartsWith(prefix + "-", StringComparison.Ordinal)) continue;
                    var target = Path.Combine(projects, Encode(to) + name[prefix.Length..]);
                    if (!Directory.Exists(target))
                        try { Directory.Move(project, target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    break;
                }
            }
        }
    }

    // Start-with-Windows and the default-browser registration pointed at pluck.exe.
    private static void MoveRegistration()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run?.GetValue("pluck") is string)
                {
                    run.SetValue("Oinzi", $"\"{Environment.ProcessPath}\"");
                    run.DeleteValue("pluck", false);
                }
            if (Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet\pluck") is { } old)
            {
                old.Dispose();
                foreach (var key in new[] { @"Software\Clients\StartMenuInternet\pluck", @"Software\Classes\pluckURL", @"Software\Classes\pluckHTML" })
                    Registry.CurrentUser.DeleteSubKeyTree(key, false);
                using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue("pluck", false);
                BrowserForm.RegisterBrowserCandidate();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }
}
