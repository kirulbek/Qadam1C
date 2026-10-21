using System.Text;
using System.Text.RegularExpressions;

namespace Qadam1C;

// Conservative whitespace edits: quoted values and comments are never rewritten.
internal static class CodeFormatting
{
    private static readonly Regex Close = new(@"^(КонецЕсли|КонецЦикла|КонецПопытки|КонецПроцедуры|КонецФункции|EndIf|EndDo|EndTry|EndProcedure|EndFunction)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Branch = new(@"^(ИначеЕсли|Иначе|Исключение|ElsIf|Else|Except)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Open = new(@"^(Процедура|Функция|Попытка|Procedure|Function|Try)\b|^(Если|If|ИначеЕсли|ElsIf)\b.*\b(Тогда|Then)\s*$|^(Для|Пока|For|While)\b.*\b(Цикл|Do)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex Assignment = new(@"^[ \t]*[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)*[ \t]*$", RegexOptions.CultureInvariant);

    internal static ExpandedSnippet Transform(EditorSnapshot value, string command, int originalCaret)
    {
        var before = value.Text[..value.Offset];
        var source = value.Text.Substring(value.Offset, value.SelectionLength);
        var inString = false;
        var depth = 0;
        foreach (var line in Lines(before))
        {
            var lex = Scan(line, ref inString);
            Advance(lex.Mask, ref depth, out _);
        }
        var rows = Lines(source);
        var original = rows.ToArray();
        var masks = new string[rows.Length];
        var protectedRows = new bool[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var lex = Scan(rows[i], ref inString);
            masks[i] = lex.Mask;
            protectedRows[i] = lex.Multiline;
            Advance(lex.Mask, ref depth, out var indent);
            if (command != "align" && !lex.Multiline)
                rows[i] = string.IsNullOrWhiteSpace(rows[i]) ? "" :
                    new string('\t', indent) + SpaceCode(rows[i].TrimStart(' ', '\t'));
        }
        if (command == "align") Align(rows, masks, protectedRows);
        var newline = source.Contains("\r\n") ? "\r\n" : "\n";
        var result = string.Join(newline, rows);
        var relative = Math.Clamp(originalCaret - value.Offset, 0, source.Length);
        var lineIndex = source[..relative].Count(c => c == '\n');
        var lineStart = relative == 0 ? 0 : source.LastIndexOf('\n', relative - 1) + 1;
        var column = relative - lineStart;
        var oldIndent = original[lineIndex].TakeWhile(char.IsWhiteSpace).Count();
        var newIndent = rows[lineIndex].TakeWhile(char.IsWhiteSpace).Count();
        var newColumn = Math.Clamp(column - oldIndent + newIndent, 0, rows[lineIndex].Length);
        var caret = rows.Take(lineIndex).Sum(s => s.Length + newline.Length) + newColumn;
        return new ExpandedSnippet(result, caret);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');
    private static (string Mask, bool Multiline) Scan(string line, ref bool quoted)
    {
        var mask = line.ToCharArray();
        var continued = quoted;
        for (var i = 0; i < line.Length; i++)
        {
            if (!quoted && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            { Array.Fill(mask, ' ', i, mask.Length - i); break; }
            if (line[i] == '"')
            {
                mask[i] = ' ';
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { mask[++i] = ' '; continue; }
                quoted = !quoted;
            }
            else if (quoted) mask[i] = ' ';
        }
        return (new string(mask), continued || quoted);
    }
    private static void Advance(string mask, ref int depth, out int indent)
    {
        var text = mask.Trim();
        if (text.StartsWith('#')) { indent = depth; return; }
        var closes = Close.IsMatch(text);
        var branch = Branch.IsMatch(text);
        if (closes || branch) depth = Math.Max(0, depth - 1);
        indent = depth;
        if (Open.IsMatch(text) || branch) depth++;
    }
    private static string SpaceCode(string line)
    {
        var result = new StringBuilder();
        var code = new StringBuilder();
        void Flush()
        {
            var text = Regex.Replace(code.ToString(), @"[ \t]*(<=|>=|<>|=|<|>)[ \t]*", " $1 ");
            text = Regex.Replace(text, @",[ \t]*", ", ");
            result.Append(text); code.Clear();
        }
        for (var i = 0; i < line.Length; i++)
        {
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            { Flush(); result.Append(line[i..]); return result.ToString(); }
            if (line[i] != '"') { code.Append(line[i]); continue; }
            Flush(); result.Append(line[i]);
            while (++i < line.Length)
            {
                result.Append(line[i]);
                if (line[i] != '"') continue;
                if (i + 1 < line.Length && line[i + 1] == '"') { result.Append(line[++i]); continue; }
                break;
            }
        }
        Flush();
        return result.ToString().TrimEnd(' ', '\t');
    }
    private static void Align(string[] rows, string[] masks, bool[] protectedRows)
    {
        var group = new List<(int Row, string Left, string Right, string Indent)>();
        void Flush()
        {
            if (group.Count > 1)
            {
                var width = group.Max(x => x.Left.Length);
                foreach (var x in group) rows[x.Row] = x.Left.PadRight(width) + " = " + x.Right;
            }
            group.Clear();
        }
        for (var i = 0; i < rows.Length; i++)
        {
            var equals = masks[i].IndexOf('=');
            if (protectedRows[i] || equals < 0 || masks[i].IndexOf('=', equals + 1) >= 0 ||
                !Assignment.IsMatch(masks[i][..equals]) ||
                (equals + 1 < masks[i].Length && masks[i][equals + 1] == '='))
            { Flush(); continue; }
            var left = rows[i][..equals].TrimEnd(' ', '\t');
            var indent = new string(left.TakeWhile(c => c is ' ' or '\t').ToArray());
            if (group.Count > 0 && group[0].Indent != indent) Flush();
            group.Add((i, left, rows[i][(equals + 1)..].TrimStart(' ', '\t'), indent));
        }
        Flush();
    }
    internal static void Check()
    {
        static string Apply(string text, string command) => Transform(new EditorSnapshot(IntPtr.Zero,
            text, 0, "", 0, 0, HasSelection: true, SelectionLength: text.Length), command, 0).Text;
        var text = "Процедура Тест()\r\nЕсли А=1 Тогда\r\nБ=\"x=y, z\"; // =  без изменений\r\nИначе\r\nБ=2;\r\nКонецЕсли;\r\nКонецПроцедуры";
        var expected = "Процедура Тест()\r\n\tЕсли А = 1 Тогда\r\n\t\tБ = \"x=y, z\"; // =  без изменений\r\n\tИначе\r\n\t\tБ = 2;\r\n\tКонецЕсли;\r\nКонецПроцедуры";
        if (Apply(text, "format") != expected || Apply(expected, "format") != expected) throw new Exception("Форматирование блоков/идемпотентность.");
        var multiline = "Текст=\"ВЫБРАТЬ\n| x=y, z\n|\";\nА=1;";
        if (Apply(multiline, "format") != "Текст=\"ВЫБРАТЬ\n| x=y, z\n|\";\nА = 1;") throw new Exception("Изменено содержимое многострочной строки.");
        var aligned = Apply("А=1;\nДлинное = \"a=b\";\nЕсли А=2 Тогда\n// X=1\nБ=2;", "align");
        if (aligned != "А       = 1;\nДлинное = \"a=b\";\nЕсли А=2 Тогда\n// X=1\nБ=2;") throw new Exception("Выравнивание затронуло условие или комментарий.");
        if (Apply(aligned, "align") != aligned) throw new Exception("Выравнивание не идемпотентно.");
    }
}
