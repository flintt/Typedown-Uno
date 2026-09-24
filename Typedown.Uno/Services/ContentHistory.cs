using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;

namespace Typedown.Uno.Services;

/// <summary>One step of undo: the whole document and where the caret was (ported from Typedown).</summary>
public sealed class HistoryEntry
{
    public string? Text { get; set; }

    public JsonNode? Cursor { get; set; }
}

/// <summary>
/// The undo stack. The editor has none of its own — it rebuilds its DOM from the document model, so the
/// browser's own undo cannot be used — and the host keeps whole-document snapshots instead.
///
/// A snapshot is not taken on every keystroke: typing keeps filling the same pending entry, which is committed
/// when the caret moves to another line, when three seconds pass, or when undo is asked for. That makes one
/// undo step a phrase rather than a letter.
/// </summary>
public sealed class ContentHistory
{
    private const int Depth = 100;

    // Snapshots are whole-document strings; the total is capped as well so a large document cannot pin
    // hundreds of megabytes of history (UTF-16: 32M chars is about 64 MB).
    private const long MaxTotalChars = 32L * 1024 * 1024;

    private readonly List<HistoryEntry> entries = new();
    private readonly DispatcherTimer commitTimer = new();
    private HistoryEntry pending = new();
    private long totalChars;
    private int index = -1;

    public ContentHistory()
    {
        commitTimer.Tick += (_, _) => CommitPending();
    }

    public bool Undoable { get; private set; }

    public bool Redoable { get; private set; }

    public event Action? Changed;

    private bool IsPending => pending.Text != null && pending.Cursor != null;

    public HistoryEntry? Undo()
    {
        if (index > 0 || (index == 0 && IsPending))
        {
            CommitPending();
            index--;
            Redoable = true;
            Undoable = index > 0;
            Changed?.Invoke();
            return entries[index];
        }
        return null;
    }

    public HistoryEntry? Redo()
    {
        if (index < entries.Count - 1)
        {
            commitTimer.Stop();
            pending = new HistoryEntry();
            index++;
            Redoable = index < entries.Count - 1;
            Undoable = true;
            Changed?.Invoke();
            return entries[index];
        }
        return null;
    }

    public void Clear()
    {
        entries.Clear();
        totalChars = 0;
        commitTimer.Stop();
        pending = new HistoryEntry();
        index = -1;
        Redoable = false;
        Undoable = false;
        Changed?.Invoke();
    }

    /// <summary>Starts a fresh stack for a document that was just opened or switched to.</summary>
    public void Init(string content)
    {
        Clear();
        CursorChange(new JsonObject
        {
            ["anchor"] = new JsonObject { ["line"] = 0, ["ch"] = 0 },
            ["focus"] = new JsonObject { ["line"] = 0, ["ch"] = 0 },
        });
        ContentChange(content);
    }

    public void CommitPending()
    {
        if (!IsPending) return;
        commitTimer.Stop();
        for (var i = index + 1; i < entries.Count; i++) totalChars -= entries[i].Text?.Length ?? 0;
        entries.RemoveRange(index + 1, entries.Count - (index + 1));
        entries.Add(pending);
        totalChars += pending.Text?.Length ?? 0;
        index++;
        while (entries.Count > Depth || (entries.Count > 1 && totalChars > MaxTotalChars))
        {
            totalChars -= entries[0].Text?.Length ?? 0;
            entries.RemoveAt(0);
            index--;
        }
        pending = new HistoryEntry();
        Redoable = false;
        Undoable = index > 0;
        Changed?.Invoke();
    }

    public void CursorChange(JsonNode? cursor)
    {
        if (pending.Text == null && index > -1)
        {
            entries[index].Cursor = cursor;
            return;
        }
        if (cursor == null) return;
        // Moving to another line ends the phrase: what was typed on the line just left becomes one undo step.
        if (IsPending && FocusLine(pending.Cursor) != FocusLine(cursor))
        {
            pending.Cursor = cursor;
            CommitPending();
            return;
        }
        pending.Cursor = cursor;
        if (pending.Text != null && entries.Count == 0)
        {
            CommitPending();
            return;
        }
        StateChange();
    }

    public void ContentChange(string? content)
    {
        if (content == null) return;
        if ((pending.Text != null && EqualsIgnoringTrailingNewlines(pending.Text, content)) ||
            (pending.Text == null && index > -1 && EqualsIgnoringTrailingNewlines(entries[index].Text, content)))
        {
            return;
        }
        pending.Text = content;
        if (pending.Cursor != null && entries.Count == 0)
        {
            CommitPending();
            return;
        }
        StateChange();
        commitTimer.Stop();
        commitTimer.Interval = TimeSpan.FromSeconds(3);
        commitTimer.Start();
    }

    private void StateChange()
    {
        Redoable = index < entries.Count - 1;
        Undoable = index > 0 || (index == 0 && IsPending);
        Changed?.Invoke();
    }

    private static int FocusLine(JsonNode? cursor)
    {
        try { return cursor?["focus"]?["line"]?.GetValue<int>() ?? -1; } catch { return -1; }
    }

    // Compares ignoring trailing newlines without allocating trimmed copies: this runs on every keystroke
    // with the whole document as input.
    private static int LengthWithoutTrailingNewlines(string s)
    {
        var n = s.Length;
        while (n > 0 && (s[n - 1] == '\n' || s[n - 1] == '\r')) n--;
        return n;
    }

    private static bool EqualsIgnoringTrailingNewlines(string? a, string? b)
    {
        if (a == null || b == null) return a == b;
        var la = LengthWithoutTrailingNewlines(a);
        var lb = LengthWithoutTrailingNewlines(b);
        return la == lb && string.CompareOrdinal(a, 0, b, 0, la) == 0;
    }
}
