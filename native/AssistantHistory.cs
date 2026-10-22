using System.Text.Json;
using System.Text.Json.Nodes;

namespace PluckWindowsNative;

public sealed partial class BrowserForm
{
    private sealed class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Engine { get; set; } = "claude";
        public string Title { get; set; } = "새 대화";
        public string? Session { get; set; }
        public List<JsonObject> Events { get; set; } = [];
    }

    private readonly List<Conversation> conversations = [];
    private readonly Dictionary<string, Conversation> currentConversations = [];
    private bool historyLoaded;
    private bool historyWritable = true;
    private readonly System.Windows.Forms.Timer historySaveTimer = new() { Interval = 600 };
    private Task historyWrite = Task.CompletedTask;
    private string HistoryFile => Path.Combine(dataDir, "assistant-history.json");

    private void LoadAssistantHistory()
    {
        if (historyLoaded) return;
        historyLoaded = true;
        try
        {
            if (File.Exists(HistoryFile))
                conversations.AddRange(JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllText(HistoryFile)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            historyWritable = false;
            PostChrome(new { type = "ai.historyError", text = "이전 대화를 읽지 못했습니다. 원본 파일을 확인해 주세요." });
        }
        foreach (var engine in Engines)
        {
            var chat = conversations.LastOrDefault(c => c.Engine == engine) ?? NewConversation(engine);
            currentConversations[engine] = chat;
            if (chat.Session != null) aiSessions[engine] = chat.Session;
            PostConversation(engine);
        }
    }

    private Conversation NewConversation(string engine)
    {
        var chat = new Conversation { Engine = engine };
        conversations.Add(chat);
        return chat;
    }

    private void SaveAssistantHistory()
    {
        if (historyWritable) historySaveTimer.Start();
    }

    private Conversation[] HistorySnapshot() => conversations.Select(c => new Conversation
    { Id = c.Id, Engine = c.Engine, Title = c.Title, Session = c.Session, Events = c.Events.ToList() }).ToArray();

    private void WriteHistory(Conversation[] snapshot)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(HistoryFile + ".tmp", JsonSerializer.Serialize(snapshot));
        File.Move(HistoryFile + ".tmp", HistoryFile, true);
    }

    private async void SaveHistoryTick(object? sender, EventArgs e)
    {
        if (!historyWrite.IsCompleted) return;
        historySaveTimer.Stop();
        var snapshot = HistorySnapshot();
        try { await (historyWrite = Task.Run(() => WriteHistory(snapshot))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!IsDisposed) PostChrome(new { type = "ai.historyError", text = "대화를 저장하지 못했습니다. 저장 공간과 파일 권한을 확인해 주세요." }); }
    }

    private void FlushAssistantHistory()
    {
        historySaveTimer.Stop();
        if (!historyLoaded || !historyWritable) return;
        try
        {
            try { historyWrite.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            WriteHistory(HistorySnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PostChrome(new { type = "ai.historyError", text = "대화를 저장하지 못했습니다. 저장 공간과 파일 권한을 확인해 주세요." }); }
    }

    private void PostConversationList(string engine) => PostChrome(new
    {
        type = "ai.history", engine, selected = currentConversations[engine].Id,
        chats = conversations.Where(c => c.Engine == engine).Reverse().Select(c => new { id = c.Id, title = c.Title }).ToArray()
    });

    private void PostConversation(string engine)
    {
        PostConversationList(engine);
        PostChrome(new { type = "ai.restore", engine, events = currentConversations[engine].Events });
    }

    private void SelectConversation(string engine, string? id = null)
    {
        if (!Engines.Contains(engine)) return;
        LoadAssistantHistory();
        var chat = id == null ? NewConversation(engine) : conversations.FirstOrDefault(c => c.Engine == engine && c.Id == id);
        if (chat == null) return;
        StopAssistant(engine);
        currentConversations[engine] = chat;
        aiSessions.Remove(engine);
        if (chat.Session != null) aiSessions[engine] = chat.Session;
        SaveAssistantHistory();
        PostConversation(engine);
    }

    private void RecordAssistant(string engine, object data)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        var entry = JsonSerializer.SerializeToNode(data)!.AsObject();
        chat.Events.Add(entry);
        if (entry["kind"]?.ToString() == "user" && chat.Title == "새 대화")
        {
            chat.Title = Clip(entry["text"]?.ToString(), 48);
            PostConversationList(engine);
        }
        // Batch disk work off the UI thread, including partial replies during long runs.
        SaveAssistantHistory();
    }
}
