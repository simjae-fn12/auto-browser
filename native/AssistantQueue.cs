namespace PluckWindowsNative;

public sealed partial class BrowserForm
{
    private sealed record QueuedPrompt(string Id, string Text, string Context, AttachedImage[] Images);

    private void PostQueue(string engine) => PostChrome(new
    {
        type = "ai.queue", engine,
        items = currentConversations[engine].Pending.Select(item => new { id = item.Id, text = item.Text, images = item.Images.Length }).ToArray()
    });

    private void SubmitAssistant(string engine, string text, bool withContext)
    {
        if (!Engines.Contains(engine) || (string.IsNullOrWhiteSpace(text) && pendingImages[engine].Count == 0)) return;
        LoadAssistantHistory();
        var context = withContext && Active() is { } tab
            ? $"[pluck: tab at submission] {tab.View.CoreWebView2?.DocumentTitle} — {tab.View.Source}\n\n" : "";
        currentConversations[engine].Pending.Add(new(Guid.NewGuid().ToString("N"), text, context, pendingImages[engine].ToArray()));
        pendingImages[engine].Clear();
        PostImages(engine);
        FlushAssistantHistory();
        PostQueue(engine);
        RunNextPrompt(engine);
    }

    private void RemoveQueuedPrompt(string engine, string id)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        chat.Pending.RemoveAll(item => item.Id == id);
        SaveAssistantHistory();
        PostQueue(engine);
    }

    private void RunNextPrompt(string engine)
    {
        if (aiRuns.ContainsKey(engine) || !currentConversations.TryGetValue(engine, out var chat) || chat.Pending.Count == 0) return;
        var next = chat.Pending[0];
        var draftImages = pendingImages[engine].ToArray();
        pendingImages[engine].Clear();
        pendingImages[engine].AddRange(next.Images);
        // Remove before starting so the durable run checkpoint cannot replay this request twice.
        chat.Pending.RemoveAt(0);
        try
        {
            StartAssistant(engine, next.Text, false, next.Context);
            if (!aiRuns.ContainsKey(engine)) chat.Pending.Insert(0, next);
        }
        finally
        {
            pendingImages[engine].Clear();
            pendingImages[engine].AddRange(draftImages);
            PostImages(engine);
            SaveAssistantHistory();
            PostQueue(engine);
        }
    }
}
