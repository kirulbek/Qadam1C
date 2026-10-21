using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed class CompletionHistory
{
    private readonly string path;
    private Dictionary<string, long> values = new();
    public CompletionHistory(string path)
    {
        this.path = path;
        try { if (File.Exists(path)) values = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private static string Key(string session, string word) => session + "|" + word.ToUpperInvariant();
    public long Score(string session, string word) => values.GetValueOrDefault(Key(session, word));
    public void Remember(string session, string word)
    {
        values[Key(session, word)] = DateTime.UtcNow.Ticks;
        values = values.OrderByDescending(p => p.Value).Take(1000).ToDictionary(p => p.Key, p => p.Value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(values));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

internal static class SmartCompletion
{
    private const string Id = @"[\p{L}_][\p{L}\p{N}_]*";
    private static readonly string[] Endings = ["КонецЕсли", "КонецЦикла", "КонецПроцедуры", "КонецФункции", "КонецПопытки"];
    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    // Keep quoted values for dynamic member analysis, but erase comments. Code
    // with quoted values erased is used for identifiers, scope and block analysis.
    internal static (string Text, bool InLiteral) Lex(string text)
    {
        var chars = text.ToCharArray();
        bool quoted = false, comment = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n') { comment = false; continue; }
            if (comment) { chars[i] = ' '; continue; }
            if (!quoted && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { comment = true; chars[i] = ' '; continue; }
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
        }
        return (new string(chars), quoted || comment);
    }

    internal static int MatchScore(string name, string prefix)
    {
        if (name.Length <= prefix.Length) return -1;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 3;
        if (prefix.Length < 2) return -1;
        var initials = new string(name.Where((c, i) => i == 0 || char.IsUpper(c) || name[i - 1] == '_').ToArray());
        if (initials.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 2;
        return name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    internal static string? Closing(string code)
    {
        var stack = new Stack<string>();
        foreach (Match m in Matches(code, @"(?<![\p{L}\p{N}_#.])(?:Если|Для|Пока|Попытка|Процедура|Функция|КонецЕсли|КонецЦикла|КонецПопытки|КонецПроцедуры|КонецФункции)(?![\p{L}\p{N}_])"))
        {
            var token = m.Value.ToUpperInvariant();
            var end = token switch
            {
                "ЕСЛИ" => "КонецЕсли", "ДЛЯ" or "ПОКА" => "КонецЦикла",
                "ПОПЫТКА" => "КонецПопытки", "ПРОЦЕДУРА" => "КонецПроцедуры",
                "ФУНКЦИЯ" => "КонецФункции", _ => null
            };
            if (end is not null) stack.Push(end);
            else if (stack.Count > 0 && stack.Peek().Equals(m.Value, StringComparison.OrdinalIgnoreCase)) stack.Pop();
        }
        return stack.TryPeek(out var closing) ? closing : null;
    }

    public static CompletionChoice[] Find(EditorSnapshot snapshot, IEnumerable<Snippet> snippets,
        MetadataIndex? metadata, CompletionHistory history, bool manual)
    {
        try { return FindCore(snapshot, snippets, metadata, history, manual); }
        catch (RegexMatchTimeoutException) { return []; }
    }

    private static CompletionChoice[] FindCore(EditorSnapshot snapshot, IEnumerable<Snippet> snippets,
        MetadataIndex? metadata, CompletionHistory history, bool manual)
    {
        var before = snapshot.Text[..snapshot.Offset];
        var lex = Lex(before);
        if (lex.InLiteral) return [];
        var code = MetadataCompletion.Code(lex.Text);
        var line = code[(code.LastIndexOf('\n') + 1)..];
        if (Regex.IsMatch(line, @"^\s*(?:Перем|Процедура|Функция)\b", RegexOptions.IgnoreCase)) return [];
        var scopeStart = Matches(code, @"(?im)^\s*(?:Процедура|Функция)\s+").LastOrDefault()?.Index ?? 0;
        var scope = lex.Text[scopeStart..];
        var scopeCode = code[scopeStart..];
        var dotted = Regex.Match(code, "(" + Id + @")\.(" + Id + @")?$");
        var closing = Closing(code[..Math.Max(0, code.Length - snapshot.Prefix.Length)]);
        IEnumerable<CompletionChoice> choices;
        if (dotted.Success)
        {
            var name = dotted.Groups[1].Value;
            var members = DynamicMembers(scope, scopeCode, name);
            // Never interpret a suffix of a longer dotted expression as a local variable.
            if (dotted.Index > 0 && code[dotted.Index - 1] == '.') members = [];
            choices = members.Select(w => new CompletionChoice(w + "  · поле", snapshot.Prefix.Length, null, w))
                .Concat(MetadataCompletion.Find(snapshot, metadata) ?? []);
        }
        else if (Regex.IsMatch(line, @"^\s*#(?:Если|ИначеЕсли)\s", RegexOptions.IgnoreCase))
        {
            choices = new[] { "Сервер", "Клиент", "НаСервере", "НаКлиенте", "ТолстыйКлиентОбычноеПриложение",
                "ТолстыйКлиентУправляемоеПриложение", "ТонкийКлиент", "ВебКлиент", "ВнешнееСоединение", "Тогда", "И", "Или", "Не" }
                .Select(w => new CompletionChoice(w, snapshot.Prefix.Length, null, w));
        }
        else
        {
            if (!manual && snapshot.Prefix.Length < 2) return [];
            choices = CompletionChoice.Find(snapshot with { Text = code }, snippets, manual)
                .Concat(CompletionWords.Find(snapshot with { Text = code }, manual)
                    .Select(w => new CompletionChoice(w, snapshot.Prefix.Length, null, w)))
                .Concat(LocalMethods(snapshot))
                .Concat((metadata?.Entries ?? []).Where(e =>
                    e.Path.StartsWith("ОбщиеМодули.", StringComparison.OrdinalIgnoreCase) &&
                    e.Path.Split('.').Length == 2)
                    .Select(e => new CompletionChoice(e.Name + "  · общий модуль",
                        snapshot.Prefix.Length, null, e.Name)))
                .Concat(Matches(code, Id).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(w => new CompletionChoice(w, snapshot.Prefix.Length, null, w)));
        }
        var recent = Matches(scopeCode[..Math.Max(0, scopeCode.Length - snapshot.Prefix.Length)], Id)
            .Cast<Match>().GroupBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Index, StringComparer.OrdinalIgnoreCase);
        string Value(CompletionChoice c) => c.Template?.MatchText ?? c.SearchText ?? c.InsertText ?? c.Label;
        return choices.Where(c => c.Template is not null || MatchScore(Value(c), snapshot.Prefix) >= 0)
            .Where(c => c.Template is not null || !Endings.Contains(Value(c), StringComparer.OrdinalIgnoreCase) ||
                Value(c).Equals(closing, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Cursor is not null)
            .DistinctBy(c => c.Template is not null ? "template:" + c.Template.Id : Value(c).ToUpperInvariant())
            .OrderByDescending(c => c.Template is not null ? 4 : MatchScore(Value(c), snapshot.Prefix))
            .ThenByDescending(c => history.Score(snapshot.Session, Value(c)))
            .ThenByDescending(c => Value(c).Equals(closing, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => recent.GetValueOrDefault(Value(c), -1))
            .ThenBy(c => Value(c).Length).ThenBy(Value, StringComparer.OrdinalIgnoreCase)
            .Take(12).ToArray();
    }

    private static IEnumerable<CompletionChoice> LocalMethods(EditorSnapshot snapshot)
    {
        var code = MetadataCompletion.Code(snapshot.Text);
        foreach (Match m in Matches(code, @"(?m)^\s*(?:Процедура|Функция)\s+(" + Id + @")\s*\(([^)]*)\)"))
        {
            var name = m.Groups[1].Value;
            var parameters = m.Groups[2].Value.Trim();
            var hasOpening = snapshot.Text[snapshot.Offset..].TrimStart().StartsWith('(');
            var insert = hasOpening ? name : name + "()";
            yield return new CompletionChoice(name + "(" + parameters.Replace("\r", "").Replace("\n", " ") + ")",
                snapshot.Prefix.Length, null, insert,
                hasOpening || parameters.Length == 0 ? insert.Length : insert.Length - 1, name);
        }
    }

    internal static string[] DynamicMembers(string source, string code, string name)
    {
        // For each row of an explicitly constructed table, reuse its known columns.
        var loops = Matches(code, @"Для\s+Каждого\s+" + Regex.Escape(name) + @"\s+Из\s+(" + Id + @")\s+Цикл");
        if (loops.Count > 0)
        {
            var loop = loops[^1];
            if (Closing(code[loop.Index..]) == "КонецЦикла") name = loop.Groups[1].Value;
        }
        var assignments = Matches(code, @"(?<![\p{L}\p{N}_.])" + Regex.Escape(name) + @"\s*=(?!=)");
        if (assignments.Count == 0) return [];
        var last = assignments[^1];
        var tail = source[last.Index..];
        var tailCode = code[last.Index..];
        var constructor = Regex.Match(tail, "^" + Regex.Escape(name) + @"\s*=\s*Новый\s+(Структура|ТаблицаЗначений)\b",
            RegexOptions.IgnoreCase);
        if (!constructor.Success) return [];
        var fields = new List<string>();
        var literal = @"""((?:""""|[^""])*)""";
        if (constructor.Groups[1].Value.Equals("Структура", StringComparison.OrdinalIgnoreCase))
        {
            var initial = Regex.Match(tail[constructor.Length..], @"^\s*\(\s*" + literal);
            if (initial.Success) fields.AddRange(initial.Groups[1].Value.Split(',').Select(s => s.Trim()));
            foreach (Match m in Matches(tail, @"(?<![\p{L}\p{N}_.])" + Regex.Escape(name) + @"\.Вставить\s*\(\s*" + literal))
                if (!string.IsNullOrWhiteSpace(tailCode.Substring(m.Index, name.Length))) fields.Add(m.Groups[1].Value);
        }
        else
        {
            foreach (Match m in Matches(tail, @"(?<![\p{L}\p{N}_.])" + Regex.Escape(name) + @"\.Колонки\.Добавить\s*\(\s*" + literal))
                if (!string.IsNullOrWhiteSpace(tailCode.Substring(m.Index, name.Length))) fields.Add(m.Groups[1].Value);
        }
        return fields.Where(f => Regex.IsMatch(f, "^" + Id + "$"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
