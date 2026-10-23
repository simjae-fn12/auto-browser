using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PluckWindowsNative;

// Per-conversation CLI options shown in the AI panel: model, reasoning effort, permission mode and working folder.
public sealed partial class BrowserForm
{
    private sealed record ModelOption(string Id, string Label, string[] Efforts);

    private static readonly ModelOption[] ClaudeModels =
    [
        new("opus", "Opus", []), new("sonnet", "Sonnet", []), new("haiku", "Haiku", []), new("fable", "Fable", [])
    ];
    private static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] CodexEfforts = ["minimal", "low", "medium", "high", "xhigh"];
    private static readonly Dictionary<string, string[]> EngineModes = new()
    {
        ["claude"] = ["restricted", "plan", "full"],
        ["codex"] = ["restricted", "readonly", "full"]
    };
    // Codex reports its models over the app-server; asked once per launch.
    private ModelOption[]? codexModels;
    private bool codexModelsLoading;

    private void PostSettings(string engine)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        if (engine == "codex" && codexModels == null) _ = LoadCodexModelsAsync();
        var models = engine == "claude" ? ClaudeModels : codexModels ?? [];
        PostChrome(new
        {
            type = "ai.settings", engine,
            model = chat.Model ?? "", effort = chat.Effort ?? "", mode = chat.Mode ?? "restricted", recording = IsRecording(SpaceOf(chat)),
            cwd = chat.Cwd ?? "", cwdName = chat.Cwd is { Length: > 0 } folder ? Path.GetFileName(folder.TrimEnd('\\', '/')) : "",
            models = models.Select(option => new { id = option.Id, label = option.Label, efforts = option.Efforts }).ToArray(),
            efforts = engine == "claude" ? ClaudeEfforts : CodexEfforts,
            modes = EngineModes[engine]
        });
    }

    private void UpdateSetting(string engine, string name, string value)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        value = value.Trim();
        switch (name)
        {
            case "model" when value.Length <= 80: chat.Model = value.Length == 0 ? null : value; break;
            case "effort" when value.Length == 0 || (engine == "claude" ? ClaudeEfforts : CodexEfforts).Contains(value):
                chat.Effort = value.Length == 0 ? null : value; break;
            case "mode" when EngineModes[engine].Contains(value): chat.Mode = value == "restricted" ? null : value; break;
            case "cwd": SetFolder(chat, value.Length == 0 ? null : value); break;
            default: return;
        }
        SaveAssistantHistory();
        PostSettings(engine);
    }

    private void PickFolder(string engine)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "AI가 작업할 폴더를 고르세요", UseDescriptionForTitle = true,
            InitialDirectory = chat.Cwd ?? AiWorkDir
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetFolder(chat, dialog.SelectedPath);
        SaveAssistantHistory();
        PostSettings(engine);
    }

    private void SetFolder(Conversation chat, string? folder)
    {
        if (folder != null && !Directory.Exists(folder)) return;
        if (string.Equals(folder, chat.Cwd, StringComparison.OrdinalIgnoreCase)) return;
        chat.Cwd = folder;
        // Claude keeps sessions per folder, so a moved conversation continues from its transcript instead.
        if (chat.Engine == "claude" && chat.Session != null) { chat.Session = null; chat.NeedsContext = true; }
    }

    private async Task LoadCodexModelsAsync()
    {
        if (codexModelsLoading || FindTool("codex") is not { } exe) return;
        codexModelsLoading = true;
        try
        {
            var models = await Task.Run(() => ReadCodexModels(exe));
            if (models.Length > 0) codexModels = models;
        }
        finally { codexModelsLoading = false; }
        if (!IsDisposed) PostSettings("codex");
    }

    private static ModelOption[] ReadCodexModels(string exe)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        IsolateAssistantEnvironment(info);
        using var process = Process.Start(info);
        if (process == null) return [];
        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"pluck","title":"pluck","version":"1"},"capabilities":null}}""");
            process.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"initialized"}""");
            process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"model/list","params":{}}""");
            process.StandardInput.Flush();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(15)) || read.Result is not { } line) break;
                JsonNode? message;
                try { message = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
                if (message?["id"]?.GetValue<int>() != 2) continue;
                return (message["result"]?["data"]?.AsArray() ?? [])
                    .Where(model => model?["hidden"]?.GetValue<bool>() != true)
                    .Select(model => new ModelOption(model!["id"]?.ToString() ?? "", model["displayName"]?.ToString() ?? model["id"]?.ToString() ?? "",
                        (model["supportedReasoningEfforts"]?.AsArray() ?? []).Select(effort => effort?["reasoningEffort"]?.ToString() ?? "").Where(effort => effort.Length > 0).ToArray()))
                    .Where(model => model.Id.Length > 0).ToArray();
            }
            return [];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or AggregateException) { return []; }
        finally { Kill(process); }
    }
}
