using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OinziBrowser;

public sealed partial class BrowserForm
{
    private sealed record SlashCommand(string Name, string Hint, string Arguments = "", string? Path = null);
    private static readonly Regex SlashPattern = new(@"^/(?<name>[\p{L}\p{N}_][\p{L}\p{N}_:.-]*)(?:\s+(?<arguments>.*))?$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private readonly Dictionary<(string Engine, string Cwd, string Account), (DateTime Time, SlashCommand[] Commands)> slashCatalogs = [];
    private readonly HashSet<string> assistantTerminals = [];

    private static (string Name, string Arguments)? ParseSlash(string text) =>
        SlashPattern.Match(text.Trim()) is { Success: true } match
            ? (match.Groups["name"].Value, match.Groups["arguments"].Value) : null;

    private static string AssistantWorkingDirectory(Conversation chat, string workspace) =>
        chat.Cwd is { Length: > 0 } folder && Directory.Exists(folder) ? folder
            : Directory.Exists(AiWorkDir) ? AiWorkDir : workspace;

    private void RenameAssistantChat(Conversation chat, string name)
    {
        chat.Title = name;
        if (sessions.ContainsKey(SpaceOf(chat))) OpenSession(SpaceOf(chat), SessionTitle(chat));
        SaveAssistantHistory();
        if (currentConversations.GetValueOrDefault(chat.Engine) == chat) PostConversationList(chat.Engine);
    }

    private static ProcessStartInfo AssistantProtocolProcess(string exe, string cwd) => new(exe)
    {
        WorkingDirectory = cwd,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    private static JsonObject CodexInitialize() => new()
    {
        ["clientInfo"] = new JsonObject { ["name"] = "oinzi", ["title"] = "Oinzi", ["version"] = "1" },
        ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
    };

    private static JsonObject CodexRequest(int id, string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    private const string CodexInitialized = """{"jsonrpc":"2.0","method":"initialized"}""";

    private static SlashCommand[] CodexSkills(JsonNode? result) =>
        (result?["data"] as JsonArray ?? []).SelectMany(entry => entry?["skills"] as JsonArray ?? [])
            .Where(skill => skill?["enabled"]?.GetValue<bool>() == true &&
                skill["name"]?.ToString() is { Length: > 0 } && skill["path"]?.ToString() is { Length: > 0 })
            .Select(skill => new SlashCommand(skill!["name"]!.ToString(),
                Clip(skill["interface"]?["shortDescription"]?.ToString() ?? skill["shortDescription"]?.ToString() ?? skill["description"]?.ToString(), 140),
                "[요청]", skill["path"]!.ToString()))
            .DistinctBy(skill => skill.Name).ToArray();

    private static SlashCommand[] ClaudeCommands(JsonNode? result) =>
        (result?["commands"] as JsonArray ?? []).SelectMany(command =>
            ((command?["aliases"] as JsonArray ?? []).Select(alias => alias?.ToString())
                .Prepend(command?["name"]?.ToString()))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new SlashCommand(name!.TrimStart('/'), Clip(command?["description"]?.ToString(), 140), command?["argumentHint"]?.ToString() ?? "")))
            .DistinctBy(command => command.Name).ToArray();

    private static string CodexGoalSummary(JsonNode? goal)
    {
        if (goal == null) return "이 대화에 등록된 GOAL이 없습니다. /goal <목표>로 등록할 수 있습니다.";
        var status = goal["status"]?.ToString();
        var message = status switch
        {
            "active" => "진행 중",
            "paused" => "일시 정지 · /goal resume으로 재개",
            "blocked" => "진행 차단 · 원인을 해결한 뒤 /goal resume",
            "budgetLimited" => "GOAL 토큰 예산 도달",
            "usageLimited" => "Codex 계정 사용 한도 도달 · /usage에서 확인하고, 사용량이 복구되면 /goal resume",
            "complete" => "완료",
            _ => status ?? ""
        };
        var budget = goal["tokenBudget"] is { } limit ? $" / {limit.GetValue<long>():N0}" : "";
        return $"GOAL · {message}\n{goal["objective"]}\n사용 토큰 {goal["tokensUsed"]?.GetValue<long>() ?? 0:N0}{budget}";
    }

    private static string CodexUsageSummary(JsonNode result)
    {
        var limits = result["rateLimits"];
        static string? Window(JsonNode? limit, string fallback)
        {
            if (limit?["usedPercent"] is not { } percent) return null;
            var minutes = limit["windowDurationMins"]?.GetValue<long>();
            var name = minutes switch
            {
                300 => "5시간", 1440 => "일간", 10080 => "주간",
                > 1440 when minutes % 1440 == 0 => $"{minutes / 1440}일",
                > 60 when minutes % 60 == 0 => $"{minutes / 60}시간",
                > 0 => $"{minutes}분",
                _ => fallback
            };
            var reset = limit["resetsAt"] is { } at
                ? $" · 초기화 {DateTimeOffset.FromUnixTimeSeconds(at.GetValue<long>()).ToLocalTime():MM/dd HH:mm}" : "";
            return $"{name} {Math.Max(0, 100 - percent.GetValue<double>()):0.#}% 남음{reset}";
        }
        var windows = new[] { Window(limits?["primary"], "기본"), Window(limits?["secondary"], "추가") }.OfType<string>();
        var summary = string.Join("\n", windows);
        if (result["ordinaryUsageAllowed"]?.GetValue<bool>() == false)
            summary = "계정의 일반 Codex 사용 한도에 도달했습니다.\n" + summary;
        return !string.IsNullOrWhiteSpace(summary) ? summary.TrimEnd() : "Codex가 계정 사용량을 반환하지 않았습니다.";
    }

    private async Task PostSlashCatalogAsync(string engine, string request)
    {
        if (!currentConversations.TryGetValue(engine, out var chat) || FindTool(engine) is not { } exe) return;
        var workspace = Path.Combine(AssistantDir, engine);
        var cwd = AssistantWorkingDirectory(chat, workspace);
        var key = (engine, cwd, ActiveAccountId(engine));
        if (!slashCatalogs.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.Time > TimeSpan.FromSeconds(30))
        {
            var info = AssistantProtocolProcess(exe, cwd);
            var commands = Array.Empty<SlashCommand>();
            try
            {
                Directory.CreateDirectory(workspace);
                if (engine == "claude")
                {
                    InstallSkills(workspace);
                    if (cwd == AiWorkDir) InstallSkills(cwd);
                }
                IsolateAssistantEnvironment(info);
                ApplyAccount(info, engine);
                commands = await Task.Run(() => ReadSlashCatalogAsync(info, engine, workspace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            cached = (DateTime.UtcNow, commands);
            if (commands.Length > 0) slashCatalogs[key] = cached;
        }
        PostChrome(new { type = "ai.commands", engine, request, commands = cached.Commands.Select(command =>
            new { name = "/" + command.Name, hint = command.Hint, arguments = command.Arguments }).ToArray() });
    }

    private static async Task<SlashCommand[]> ReadSlashCatalogAsync(ProcessStartInfo info, string engine, string workspace)
    {
        var codex = engine == "codex";
        if (codex)
        {
            AddCodexDaemonFlag(info);
            info.ArgumentList.Add("app-server");
        }
        else
        {
            foreach (var arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" })
                info.ArgumentList.Add(arg);
            if (info.WorkingDirectory != workspace) { info.ArgumentList.Add("--add-dir"); info.ArgumentList.Add(workspace); }
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("CLI를 시작하지 못했습니다.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        try
        {
            _ = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteLineAsync(codex
                ? CodexRequest(1, "initialize", CodexInitialize()).ToJsonString()
                : """{"type":"control_request","request_id":"oinzi-commands","request":{"subtype":"initialize"}}""");
            await process.StandardInput.FlushAsync(token);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                JsonNode? message;
                try { message = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (codex)
                {
                    switch (message?["id"]?.ToString())
                    {
                        case "1" when message["result"] != null:
                            await process.StandardInput.WriteLineAsync(CodexInitialized);
                            await process.StandardInput.WriteLineAsync(CodexRequest(2, "skills/list",
                                new JsonObject { ["cwds"] = new JsonArray(info.WorkingDirectory), ["forceReload"] = true }).ToJsonString());
                            await process.StandardInput.FlushAsync(token);
                            break;
                        case "1": return [];
                        case "2": return CodexSkills(message["result"]);
                    }
                }
                else if (message?["type"]?.ToString() == "control_response" &&
                    message["response"]?["request_id"]?.ToString() == "oinzi-commands")
                    return ClaudeCommands(message["response"]?["response"]);
            }
            return [];
        }
        finally { Kill(process); }
    }

    private async Task OpenAssistantTerminalAsync(string engine)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        void Note(string message) => PostAssistant(chat, new { kind = "note", text = message });
        if (FindTool(engine) is not { } exe) { Note($"{engine} CLI를 찾을 수 없습니다."); return; }
        if (aiRuns.ContainsKey(chat.Id) || !assistantTerminals.Add(chat.Id))
        {
            Note("이 대화가 사용 중입니다. 실행을 마친 뒤 터미널을 열어 주세요.");
            return;
        }
        try
        {
            var workspace = PrepareAssistantWorkspace(engine);
            var cwd = AssistantWorkingDirectory(chat, workspace);
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
            IsolateAssistantEnvironment(info);
            info.Environment.Remove("TERM");
            info.Environment.Remove("NO_COLOR");
            var account = ApplyAccount(info, engine);
            if (connectionFile != null) info.Environment["OINZI_CONNECTION"] = connectionFile;
            var browserSession = OpenSession(SpaceOf(chat), SessionTitle(chat), "panel");
            info.Environment["OINZI_SESSION"] = browserSession.Id;
            info.Environment["OINZI_SESSION_LABEL"] = browserSession.Label;
            if (chat.Session is { } session && account == chat.SessionAccount &&
                (engine == "codex" || string.Equals(cwd, chat.SessionCwd ?? workspace, StringComparison.OrdinalIgnoreCase)))
            {
                info.ArgumentList.Add(engine == "codex" ? "resume" : "--resume");
                info.ArgumentList.Add(session);
            }
            else if (chat.Session != null)
            {
                Note("이 대화에서 사용한 계정과 작업 폴더로 전환하면 같은 세션을 터미널에서 열 수 있습니다.");
                return;
            }
            if (chat.Model is { Length: > 0 } model) { info.ArgumentList.Add("--model"); info.ArgumentList.Add(model); }
            if (engine == "codex")
            {
                info.ArgumentList.Add("--sandbox");
                info.ArgumentList.Add(chat.Mode switch { "full" => "danger-full-access", "readonly" => "read-only", _ => "workspace-write" });
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("sandbox_workspace_write.network_access=true");
            }
            else
            {
                info.ArgumentList.Add("--permission-mode");
                info.ArgumentList.Add(chat.Mode switch { "full" => "bypassPermissions", "plan" => "plan", _ => "acceptEdits" });
            }
            using var process = Process.Start(info) ?? throw new InvalidOperationException("CLI를 시작하지 못했습니다.");
            Note($"{engine} 터미널을 열었습니다. 터미널에 /를 입력하면 전체 명령을 볼 수 있습니다.");
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Note("터미널을 열지 못했습니다. " + ex.Message);
        }
        finally { assistantTerminals.Remove(chat.Id); }
    }
}
