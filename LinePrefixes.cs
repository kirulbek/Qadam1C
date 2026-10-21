namespace Qadam1C;

internal static class LinePrefixes
{
    internal static bool Supports(string command) => command is
        "comment-add" or "comment-remove" or "pipe-add" or "pipe-remove";

    internal static ExpandedSnippet Transform(EditorSnapshot snapshot, string command)
    {
        if (!Supports(command)) throw new ArgumentException("Неизвестная команда", nameof(command));
        var selected = snapshot.Text.Substring(snapshot.Offset, snapshot.SelectionLength);
        var marker = command.StartsWith("comment-", StringComparison.Ordinal) ? "//" : "|";
        var add = command.EndsWith("-add", StringComparison.Ordinal);
        var result = new System.Text.StringBuilder();
        // Keep every newline and all original indentation exactly as they were.
        for (var start = 0; start < selected.Length;)
        {
            var end = selected.IndexOf('\n', start);
            if (end < 0) end = selected.Length;
            var contentEnd = end > start && selected[end - 1] == '\r' ? end - 1 : end;
            var position = start;
            while (position < contentEnd && selected[position] is ' ' or '\t') position++;
            var hasMarker = selected.AsSpan(position, contentEnd - position).StartsWith(marker.AsSpan(), StringComparison.Ordinal);
            result.Append(selected, start, position - start);
            if (add && !hasMarker) result.Append(marker);
            var copyStart = !add && hasMarker ? position + marker.Length : position;
            result.Append(selected, copyStart, contentEnd - copyStart);
            result.Append(selected, contentEnd, end - contentEnd);
            if (end < selected.Length) result.Append('\n');
            start = end + 1;
        }
        return new ExpandedSnippet(result.ToString(), 0);
    }

    internal static void Check()
    {
        static string Apply(string value, string command) => Transform(new EditorSnapshot(
            IntPtr.Zero, value, 0, "", 0, 0, HasSelection: true, SelectionLength: value.Length), command).Text;
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var original = "\tА = \"// и |\";" + newline + "  Б = 2;" + newline + "";
            foreach (var kind in new[] { "comment", "pipe" })
            {
                var marker = kind == "comment" ? "//" : "|";
                var added = Apply(original, kind + "-add");
                var expected = "\t" + marker + "А = \"// и |\";" + newline + "  " + marker + "Б = 2;" + newline;
                if (added != expected || Apply(added, kind + "-add") != added ||
                    Apply(added, kind + "-remove") != original || Apply(original, kind + "-remove") != original)
                    throw new Exception("Ошибка добавления/снятия префиксов строк: " + kind);
            }
        }
        if (Apply("//А\nБ\n\t", "comment-add") != "//А\n//Б\n\t//" ||
            Apply("||А\nБ", "pipe-remove") != "|А\nБ")
            throw new Exception("Ошибка обработки смешанных или пустых строк.");
    }
}
