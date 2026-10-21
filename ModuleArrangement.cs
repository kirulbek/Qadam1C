namespace Qadam1C;

// Changes remain in memory until the user explicitly applies the arrangement.
internal sealed class ModuleArrangement(EditorSnapshot original)
{
    internal EditorSnapshot Original { get; } = original;
    internal string Text { get; private set; } = original.Text;
    internal int Changes { get; private set; }
    private int cursor;

    internal void Stage(MethodMovePlan plan)
    {
        if (Text[..plan.Start] + plan.Replacement + Text[(plan.Start + plan.Length)..] != plan.Result)
            throw new InvalidOperationException("План перестановки устарел.");
        Text = plan.Result;
        cursor = plan.Start + plan.Cursor;
        Changes++;
    }

    internal MethodMovePlan BuildPlan()
    {
        // Keep the unchanged prefix and suffix outside the single editor replacement.
        var start = 0;
        while (start < Original.Text.Length && start < Text.Length && Original.Text[start] == Text[start]) start++;
        start = Math.Min(start, cursor);
        var end = Original.Text.Length;
        var resultEnd = Text.Length;
        while (end > start && resultEnd > Math.Max(start, cursor) && Original.Text[end - 1] == Text[resultEnd - 1])
        { end--; resultEnd--; }
        // UI Automation treats CRLF as one character; never split a line ending.
        if (start > 0 && start < Text.Length && Text[start - 1] == '\r' && Text[start] == '\n') start--;
        if (resultEnd > 0 && resultEnd < Text.Length && Text[resultEnd - 1] == '\r' && Text[resultEnd] == '\n')
        { resultEnd++; end++; }
        return new(start, end - start, Text[start..resultEnd], cursor - start, Text);
    }
}
