using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Qadam1C;

internal sealed record CompletionChoice(string Label, int ReplaceLength, Snippet? Template = null, string? InsertText = null,
    int? Cursor = null, string? SearchText = null)
{
    public static CompletionChoice[] Find(EditorSnapshot snapshot, IEnumerable<Snippet> snippets, bool manual)
    {
        var before = snapshot.Text[..snapshot.Offset];
        var matches = new List<CompletionChoice>();
        foreach (var snippet in snippets.Where(s => s.Enabled && s.Trigger.Length > 0))
        {
            var trigger = snippet.MatchText;
            for (var length = Math.Min(trigger.Length, before.Length); length >= 2; length--)
            {
                var start = before.Length - length;
                if (start > 0 && (char.IsLetterOrDigit(before[start - 1]) || before[start - 1] == '_')) continue;
                if (before.AsSpan(start).Equals(trigger.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new("▸ " + snippet.Name, length, snippet));
                    break;
                }
            }
        }
        return matches.OrderByDescending(c => c.ReplaceLength).ThenBy(c => c.Template!.MatchText.Length)
            .Concat(CompletionWords.Find(snapshot, manual).Select(w => new CompletionChoice(w, snapshot.Prefix.Length)))
            .Take(12).ToArray();
    }
}

internal static class SnippetInsertion
{
    public static async Task<bool> Insert(EditorSnapshot expected, int removeLength, ExpandedSnippet snippet, bool forActions = false)
    {
        // Paste is a single editor operation, avoiding autoindent on every Enter.
        // Materialize clipboard data before taking ownership, then restore only if
        // nobody else has changed it during this insertion.
        var previous = Clipboard.GetDataObject();
        var saved = new DataObject();
        if (previous is not null)
            foreach (var format in previous.GetFormats(false))
            {
                var value = previous.GetData(format, false);
                if (value is not null) saved.SetData(format, false, value);
            }
        if (snippet.Text.Length == 0) throw new InvalidOperationException("После удаления полей шаблон пуст.");
        var selectedLength = forActions ? expected.SelectionLength : 0;
        var desired = expected.Text[..(expected.Offset - removeLength)] + snippet.Text + expected.Text[(expected.Offset + selectedLength)..];
        uint sequence = 0;
        try
        {
            Clipboard.SetText(snippet.Text);
            sequence = GetClipboardSequenceNumber();
            // Do not send a synthetic chord while the user's physical Tab is held.
            for (var i = 0; i < (forActions ? 150 : 30) &&
                (CompletionNative.KeyDown(9) || (forActions && (CompletionNative.KeyDown(0x11) ||
                CompletionNative.KeyDown(0x12) || CompletionNative.KeyDown(0x10) || CompletionNative.KeyDown(0x44)))); i++)
                await Task.Delay(10);
            var fresh = await Task.Run(() => forActions ? EditorReader.ReadForActions() : EditorReader.Read());
            if (fresh is null || fresh.Window != expected.Window || fresh.Text != expected.Text || fresh.Offset != expected.Offset ||
                fresh.SelectionLength != selectedLength ||
                CompletionNative.KeyDown(9) || CompletionNative.KeyDown(0x10) || CompletionNative.KeyDown(0x11) || CompletionNative.KeyDown(0x12))
            {
                InteractionDiagnostics.Write($"insert-cancel: reader={EditorReader.LastStatus}; windowChanged={fresh?.Window != expected.Window}; textChanged={fresh?.Text != expected.Text}; offsetChanged={fresh?.Offset != expected.Offset}; tab={CompletionNative.KeyDown(9)}; shift={CompletionNative.KeyDown(0x10)}; ctrl={CompletionNative.KeyDown(0x11)}; alt={CompletionNative.KeyDown(0x12)}");
                return false;
            }
            if (GetClipboardSequenceNumber() != sequence) { InteractionDiagnostics.Write("insert-cancel: clipboard-changed"); return false; }
            SelectPrefix(expected, removeLength, selectedLength);
            if (CompletionNative.GetForegroundWindow() != expected.Window) return false;
            CompletionNative.Paste();
            EditorSnapshot? after = null;
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(40);
                after = await Task.Run(() => forActions ? EditorReader.ReadForActions() : EditorReader.Read());
                if (after is not null && Normalize(after.Text) == Normalize(desired)) break;
                if (CompletionNative.GetForegroundWindow() != expected.Window) return false;
            }
            if (after is null || Normalize(after.Text) != Normalize(desired))
                throw new InvalidOperationException("Вставка отправлена, но текст не подтверждён. Проверьте результат; повтор не выполняется.");
            var end = Normalize(expected.Text[..(expected.Offset - removeLength)] + snippet.Text).Length;
            if (Normalize(after.Text[..after.Offset]).Length != end || after.Window != expected.Window) return false;
            var prefix = expected.Text[..(expected.Offset - removeLength)] + snippet.Text[..snippet.Cursor];
            SelectCaret(expected.Window, desired, prefix);
            return true;
        }
        finally
        {
            if (sequence != 0 && GetClipboardSequenceNumber() == sequence)
            {
                if (previous is null) Clipboard.Clear();
                else Clipboard.SetDataObject(saved, true);
            }
        }
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private static void SelectCaret(IntPtr window, string desired, string prefix)
    {
        if (CompletionNative.GetForegroundWindow() != window) return;
        var document = EditorReader.FocusedDocument(window);
        if (document is null || !document.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return;
        var pattern = (TextPattern)raw;
        var range = pattern.DocumentRange.Clone();
        if (Normalize(range.GetText(-1)) != Normalize(desired)) return;
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, Normalize(prefix).Length);
        if (Normalize(range.GetText(-1)) != Normalize(prefix))
            throw new InvalidOperationException("Шаблон вставлен, но редактор не подтвердил место курсора.");
        range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
        range.Select();
    }
    private static void SelectPrefix(EditorSnapshot expected, int length, int selectedLength = 0)
    {
        var document = EditorReader.FocusedDocument(expected.Window);
        if (document is null || !document.TryGetCurrentPattern(TextPattern.Pattern, out var raw))
            throw new InvalidOperationException("Редактор недоступен.");
        var pattern = (TextPattern)raw;
        var ranges = pattern.GetSelection();
        if (ranges.Length != 1 || ranges[0].GetText(-1) != expected.Text.Substring(expected.Offset, selectedLength))
            throw new InvalidOperationException("Позиция курсора изменилась.");
        var range = ranges[0].Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -length);
        if (range.GetText(-1) != expected.Text[(expected.Offset - length)..(expected.Offset + selectedLength)])
            throw new InvalidOperationException("Не удалось выделить сокращение.");
        range.Select();
        if (pattern.GetSelection().Single().GetText(-1) != expected.Text[(expected.Offset - length)..(expected.Offset + selectedLength)])
            throw new InvalidOperationException("Редактор не подтвердил выделение.");
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
