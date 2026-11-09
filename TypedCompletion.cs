using System.IO;
using System.Text.RegularExpressions;

namespace Qadam1C;

// Bounded static interpretation. No BSL is executed and unknown values never acquire guessed fields.
internal static class TypedCompletion
{
    internal sealed record Token(string Text, int Position, bool Literal = false)
    { internal bool Is(string value) => !Literal && Text.Equals(value, StringComparison.OrdinalIgnoreCase); }
    internal sealed class Shape(string kind, string path = "")
    {
        internal string Kind = kind, Path = path;
        internal Dictionary<string, Shape> Fields = new(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly Shape Unknown = new("unknown");
    internal static List<Token> Lex(string text)
    {
        var result = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            var start = i;
            if (text[i] == '"')
            {
                i++; var value = new System.Text.StringBuilder();
                while (i < text.Length)
                {
                    if (text[i] == '"') { i++; if (i < text.Length && text[i] == '"') { value.Append('"'); i++; continue; } break; }
                    value.Append(text[i++]);
                }
                result.Add(new(value.ToString(), start, true));
            }
            else if (char.IsLetterOrDigit(text[i]) || text[i] == '_')
            { while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++; result.Add(new(text[start..i], start)); }
            else result.Add(new(text[i++].ToString(), start));
        }
        return result;
    }
    private static bool Identifier(Token t) => !t.Literal && Regex.IsMatch(t.Text, @"^[\p{L}_][\p{L}\p{N}_]*$");
    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase) {
        ["Документ"] = "Документы", ["Справочник"] = "Справочники", ["Перечисление"] = "Перечисления",
        ["ПланСчетов"] = "ПланыСчетов", ["ПланВидовХарактеристик"] = "ПланыВидовХарактеристик",
        ["БизнесПроцесс"] = "БизнесПроцессы", ["Задача"] = "Задачи" };
    private sealed class Schema
    {
        internal Dictionary<string, MetadataEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, List<MetadataEntry>> Children = new(StringComparer.OrdinalIgnoreCase);
        internal Schema(IEnumerable<MetadataIndex> sources)
        {
            foreach (var source in sources) foreach (var e in source.Entries)
            {
                if (Entries.TryGetValue(e.Path, out var old) && e.Types.Length == 0) Entries[e.Path] = e with { Types = old.Types };
                else Entries[e.Path] = e;
            }
            foreach (var e in Entries.Values)
            {
                var parts = e.Path.Split('.');
                if (parts.Length < 2) continue;
                var parent = parts.Length == 2 ? parts[0] : string.Join('.', parts[..^2]);
                if (!Children.TryGetValue(parent, out var list)) Children[parent] = list = [];
                list.Add(e);
            }
        }
        internal Shape Type(string text)
        {
            var name = text.Trim();
            var dot = name.IndexOf('.');
            if (dot < 0) return new(name.Equals("Структура", StringComparison.OrdinalIgnoreCase) ? "struct" :
                name.Equals("ТаблицаЗначений", StringComparison.OrdinalIgnoreCase) ? "table" : name);
            var kind = name[..dot].Replace("Ссылка", "").Replace("Объект", "");
            return Roots.TryGetValue(kind, out var root) ? new(name[..dot].Contains("Объект") ? "object" : "reference", root + name[dot..]) : Unknown;
        }
        internal IEnumerable<(string Name, Shape Type)> Members(Shape shape)
        {
            if (shape.Kind is "struct" or "row") return shape.Fields.Select(f => (f.Key, f.Value));
            if (shape.Kind == "movements")
                return Entries.GetValueOrDefault(shape.Path)?.Registers.Select(r =>
                {
                    var parts = r.Split('.');
                    var root = parts[0] switch { "РегистрНакопления" => "РегистрыНакопления", "РегистрСведений" => "РегистрыСведений", "РегистрБухгалтерии" => "РегистрыБухгалтерии", "РегистрРасчета" => "РегистрыРасчета", _ => parts[0] };
                    return (parts.Last(), new Shape("recordset", root + "." + parts.Last()));
                }) ?? [];
            if (shape.Kind is not ("object" or "reference" or "root" or "enum")) return [];
            if (!Children.TryGetValue(shape.Path, out var list)) return [];
            return list.Where(e => shape.Kind == "root" || e.Path.Contains(".Реквизиты.") || e.Path.Contains(".Измерения.") ||
                e.Path.Contains(".Ресурсы.") || e.Path.Contains(".ТабличныеЧасти.") || e.Path.Contains(".ЗначенияПеречисления."))
                .Select(e => (e.Name, Entry(shape, e)));
        }
        private Shape Entry(Shape owner, MetadataEntry e)
        {
            if (owner.Kind == "root") return new(owner.Path == "Перечисления" ? "enum" : "manager", e.Path);
            if (e.Path.Contains(".ТабличныеЧасти.") && e.Path.Split('.').Length == 4)
            {
                var table = new Shape("table", e.Path);
                if (Children.TryGetValue(e.Path, out var fields)) foreach (var f in fields) table.Fields[f.Name] = Entry(new("row"), f);
                return table;
            }
            // Mixed reference/scalar and multiple references remain ambiguous.
            return e.Types.Length == 1 ? Type(e.Types[0]) : Unknown;
        }
        internal Shape Member(Shape owner, string name) => Members(owner).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Type ?? Unknown;
    }
    private static readonly object schemaGate = new();
    private static readonly List<(MetadataIndex[] Sources, Schema Schema)> schemas = [];
    private static Schema GetSchema(MetadataIndex[] sources)
    {
        lock (schemaGate)
        {
            var found = schemas.FirstOrDefault(c => c.Sources.Length == sources.Length && c.Sources.Zip(sources).All(p => ReferenceEquals(p.First, p.Second)));
            if (found.Schema is not null) return found.Schema;
            var schema = new Schema(sources); if (schemas.Count >= 8) schemas.RemoveAt(0); schemas.Add((sources, schema)); return schema;
        }
    }
    private static Shape Merge(Shape a, Shape b, int depth = 0)
    {
        if (depth > 8 || a.Kind != b.Kind || a.Path != b.Path) return Unknown;
        var merged = new Shape(a.Kind, a.Path);
        foreach (var f in a.Fields) if (b.Fields.TryGetValue(f.Key, out var value)) merged.Fields[f.Key] = Merge(f.Value, value, depth + 1);
        return merged;
    }
    private static Dictionary<string, Shape> Clone(Dictionary<string, Shape> values)
    {
        var seen = new Dictionary<Shape, Shape>(ReferenceEqualityComparer.Instance);
        Shape Copy(Shape value, int depth)
        {
            if (depth > 12) return Unknown;
            if (seen.TryGetValue(value, out var old)) return old;
            var result = new Shape(value.Kind, value.Path); seen[value] = result;
            foreach (var f in value.Fields) result.Fields[f.Key] = Copy(f.Value, depth + 1);
            return result;
        }
        return values.ToDictionary(v => v.Key, v => Copy(v.Value, 0), StringComparer.OrdinalIgnoreCase);
    }
    private static Dictionary<string, Shape> MergeState(Dictionary<string, Shape> a, Dictionary<string, Shape> b) =>
        a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k, k => Merge(a.GetValueOrDefault(k, Unknown), b.GetValueOrDefault(k, Unknown)), StringComparer.OrdinalIgnoreCase);
    private sealed class Engine(Schema schema, string moduleText, MetadataIndex[] sources, string title, int depth = 0, System.Diagnostics.Stopwatch? sharedClock = null)
    {
        internal Dictionary<string, Shape> Variables = new(StringComparer.OrdinalIgnoreCase);
        internal List<Shape> Returns = [];
        internal bool FinalReturn;
        private int work;
        private readonly System.Diagnostics.Stopwatch clock = sharedClock ?? System.Diagnostics.Stopwatch.StartNew();
        private int expressionDepth;
        private void Budget() { if (++work > 30000 || clock.ElapsedMilliseconds > 350) throw new TimeoutException(); }
        private sealed class Branch(Dictionary<string, Shape> start, string kind)
        {
            internal Dictionary<string, Shape> Start = start;
            internal Dictionary<string, Shape>? Result;
            internal bool HasElse;
            internal string Kind = kind;
            internal string? Iterator;
        }
        internal void Run(List<Token> tokens)
        {
            var blocks = new Stack<Branch>(); var i = 0;
            while (i < tokens.Count)
            {
                Budget(); var t = tokens[i];
                if (t.Is("Если"))
                { blocks.Push(new(Clone(Variables), "if")); SkipUntil(tokens, ref i, "Тогда"); continue; }
                if (t.Is("Иначе") || t.Is("ИначеЕсли"))
                {
                    if (blocks.TryPeek(out var branch) && branch.Kind == "if")
                    { branch.Result = branch.Result is null ? Clone(Variables) : MergeState(branch.Result, Variables); Variables = Clone(branch.Start); if (t.Is("Иначе")) branch.HasElse = true; }
                    if (t.Is("ИначеЕсли")) SkipUntil(tokens, ref i, "Тогда"); else i++; continue;
                }
                if (t.Is("КонецЕсли"))
                {
                    if (blocks.Count > 0 && blocks.Peek().Kind == "if")
                    { var b = blocks.Pop(); var result = b.Result is null ? Variables : MergeState(b.Result, Variables); Variables = b.HasElse ? result : MergeState(b.Start, result); }
                    i++; continue;
                }
                if (t.Is("Для") || t.Is("Пока"))
                {
                    var b = new Branch(Clone(Variables), "loop"); blocks.Push(b);
                    if (i + 3 < tokens.Count && tokens[i + 1].Is("Каждого") && tokens[i + 3].Is("Из"))
                    { b.Iterator = tokens[i + 2].Text; var p = i + 4; Variables[b.Iterator] = Row(Expression(tokens, ref p)); }
                    SkipUntil(tokens, ref i, "Цикл"); continue;
                }
                if (t.Is("КонецЦикла"))
                {
                    if (blocks.Count > 0 && blocks.Peek().Kind == "loop") { var b = blocks.Pop(); Variables = MergeState(b.Start, Variables); if (b.Iterator is not null) Variables.Remove(b.Iterator); }
                    i++; continue;
                }
                if (t.Is("Попытка") || t.Is("Исключение") || t.Is("КонецПопытки"))
                { Variables.Clear(); i++; continue; }
                if (t.Is("Возврат"))
                { i++; var value = Expression(tokens, ref i); Returns.Add(i < tokens.Count && !tokens[i].Is(";") ? Unknown : value); if (blocks.Count == 0) FinalReturn = true; }
                else if (Identifier(t) && i + 1 < tokens.Count && tokens[i + 1].Is("="))
                { i += 2; var value = Expression(tokens, ref i); Variables[t.Text] = i < tokens.Count && !tokens[i].Is(";") ? Unknown : value; }
                else if (t.Is("Перем"))
                { i++; while (i < tokens.Count && !tokens[i].Is(";")) { if (Identifier(tokens[i])) Variables[tokens[i].Text] = Unknown; i++; } }
                else
                {
                    var old = i; var value = Expression(tokens, ref i);
                    if (i < tokens.Count && tokens[i].Is("=") && i >= old + 3 && tokens[i - 2].Is("."))
                    {
                        var field = tokens[i - 1].Text; var p = old;
                        var parent = Expression(tokens.Take(i - 2).ToList(), ref p); i++;
                        var right = Expression(tokens, ref i);
                        if (parent.Kind is "struct" or "row" && parent.Fields.ContainsKey(field)) parent.Fields[field] = right;
                    }
                    if (old == i) i++;
                }
                if (i < tokens.Count && tokens[i].Is(";")) i++;
            }
        }
        private static void SkipUntil(List<Token> tokens, ref int i, string value)
        { while (i < tokens.Count && !tokens[i++].Is(value)) { } }
        private Shape Row(Shape table) => table.Kind == "table" ? new Shape("row", table.Path) { Fields = table.Fields } : Unknown;
        internal Shape Expression(List<Token> tokens, ref int i)
        {
            if (++expressionDepth > 96) throw new TimeoutException();
            try { return ExpressionCore(tokens, ref i); }
            finally { expressionDepth--; }
        }
        private Shape CurrentObject()
        {
            var m = Regex.Match(title, @"^(Документ|Справочник)\s+([\p{L}\p{N}_]+):");
            return m.Success ? new("object", Roots[m.Groups[1].Value] + "." + m.Groups[2].Value) : Unknown;
        }
        private Shape ExpressionCore(List<Token> tokens, ref int i)
        {
            Budget(); if (i >= tokens.Count) return Unknown;
            var first = tokens[i++]; Shape value;
            if (first.Literal) value = new("Строка", first.Text);
            else if (first.Is("Новый"))
            {
                if (i >= tokens.Count) return Unknown;
                var kind = tokens[i++].Text;
                var args = Arguments(tokens, ref i);
                value = kind.Equals("Структура", StringComparison.OrdinalIgnoreCase) ? new("struct") :
                    kind.Equals("ТаблицаЗначений", StringComparison.OrdinalIgnoreCase) ? new("table") : schema.Type(kind);
                if (kind.Equals("ОписаниеТипов", StringComparison.OrdinalIgnoreCase) && args.Count == 1 && args[0].Kind == "Строка" && !args[0].Path.Contains(',')) value = schema.Type(args[0].Path);
                if (value.Kind == "struct" && args.Count > 0 && args[0].Kind == "Строка")
                { var keys = args[0].Path.Split(','); for (var n = 0; n < keys.Length; n++) if (ValidName(keys[n].Trim())) value.Fields[keys[n].Trim()] = args.ElementAtOrDefault(n + 1) ?? Unknown; }
            }
            else if (first.Is("(")) { value = Expression(tokens, ref i); if (i < tokens.Count && tokens[i].Is(")")) i++; }
            else if (!Identifier(first)) value = new("Число");
            else if (Variables.TryGetValue(first.Text, out var variable)) value = variable;
            else if (MetadataReport.Roots.Contains(first.Text, StringComparer.OrdinalIgnoreCase)) value = new("root", first.Text);
            else if (first.Is("Объект") || first.Is("ЭтотОбъект"))
                value = CurrentObject();
            else if (first.Is("Движения")) value = new("movements", CurrentObject().Path);
            else if (i < tokens.Count && tokens[i].Is("(")) value = CallFunction(null, first.Text, Arguments(tokens, ref i));
            else { value = schema.Member(CurrentObject(), first.Text); if (value.Kind == "unknown") value = new("module", first.Text); }
            while (i < tokens.Count)
            {
                Budget();
                if (tokens[i].Is("["))
                { i++; var key = Expression(tokens, ref i); if (i < tokens.Count && tokens[i].Is("]")) i++; value = value.Kind == "struct" ? value.Fields.GetValueOrDefault(key.Path, Unknown) : Row(value); continue; }
                if (!tokens[i].Is(".") || i + 1 >= tokens.Count || !Identifier(tokens[i + 1])) break;
                var name = tokens[i + 1].Text; i += 2;
                if (i < tokens.Count && tokens[i].Is("(")) value = Call(value, name, Arguments(tokens, ref i));
                else if (value.Kind == "table" && name.Equals("Колонки", StringComparison.OrdinalIgnoreCase)) value = new("columns") { Fields = value.Fields };
                else value = schema.Member(value, name);
            }
            return value;
        }
        private List<Shape> Arguments(List<Token> tokens, ref int i)
        {
            var args = new List<Shape>(); if (i >= tokens.Count || !tokens[i].Is("(")) return args;
            i++; while (i < tokens.Count && !tokens[i].Is(")"))
            {
                Budget(); var old = i; args.Add(Expression(tokens, ref i));
                if (i < tokens.Count && tokens[i].Is(",")) i++;
                else if (i == old) i++;
                else if (i < tokens.Count && !tokens[i].Is(")")) { args[^1] = Unknown; while (i < tokens.Count && !tokens[i].Is(",") && !tokens[i].Is(")")) i++; }
            }
            if (i < tokens.Count && tokens[i].Is(")")) i++; return args;
        }
        private Shape Call(Shape owner, string name, List<Shape> args)
        {
            var method = name.ToUpperInvariant();
            if (owner.Kind == "manager" && method is "СОЗДАТЬДОКУМЕНТ" or "СОЗДАТЬЭЛЕМЕНТ" or "СОЗДАТЬГРУППУ") return new("object", owner.Path);
            if (owner.Kind == "manager" && method is "НАЙТИПОКОДУ" or "НАЙТИПОНАИМЕНОВАНИЮ" or "НАЙТИПОНОМЕРУ" or "ПОЛУЧИТЬССЫЛКУ" or "ПУСТАЯССЫЛКА") return new("reference", owner.Path);
            if (owner.Kind == "reference" && method == "ПОЛУЧИТЬОБЪЕКТ") return new("object", owner.Path);
            if (owner.Kind == "recordset" && method == "ДОБАВИТЬ") return new("object", owner.Path);
            if (owner.Kind == "table" && method is "ДОБАВИТЬ" or "ВСТАВИТЬ" or "ПОЛУЧИТЬ" or "НАЙТИ") return Row(owner);
            if (owner.Kind == "table" && method is "СКОПИРОВАТЬ" or "СКОПИРОВАТЬКОЛОНКИ")
            {
                var selection = args.ElementAtOrDefault(method == "СКОПИРОВАТЬ" ? 1 : 0);
                var copy = new Shape("table");
                if (selection is null || selection.Kind == "module" && selection.Path.Equals("Неопределено", StringComparison.OrdinalIgnoreCase)) copy.Fields = new(owner.Fields, StringComparer.OrdinalIgnoreCase);
                else if (selection.Kind == "Строка")
                    foreach (var key in selection.Path.Split(',').Select(s => s.Trim()))
                        if (owner.Fields.TryGetValue(key, out var field)) copy.Fields[key] = field;
                return copy;
            }
            if (owner.Kind == "columns" && method == "ДОБАВИТЬ" && args.Count > 0 && args[0].Kind == "Строка" && ValidName(args[0].Path))
                owner.Fields[args[0].Path] = args.ElementAtOrDefault(1) ?? Unknown;
            if (owner.Kind == "struct" && method == "ВСТАВИТЬ" && args.Count > 0 && args[0].Kind == "Строка" && ValidName(args[0].Path)) owner.Fields[args[0].Path] = args.ElementAtOrDefault(1) ?? Unknown;
            if (owner.Kind is "struct" or "columns" && method == "УДАЛИТЬ" && args.Count > 0)
            { if (args[0].Kind == "Строка") owner.Fields.Remove(args[0].Path); else owner.Fields.Clear(); }
            if (owner.Kind is "struct" or "columns" && method == "ОЧИСТИТЬ") owner.Fields.Clear();
            return owner.Kind == "module" ? CallFunction(owner.Path, name, args) : Unknown;
        }
        private Shape CallFunction(string? module, string name, List<Shape> args)
        {
            if (depth >= 5) return Unknown;
            var text = module is null ? moduleText : FunctionSources.Read(sources, module, name);
            if (text is null) return Unknown;
            var matches = ModuleOutline.Parse(text).Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                (module is null || m.Exported) && m.Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return Unknown;
            var method = matches[0];
            var all = Lex(text); var start = all.FindIndex(t => t.Position == method.Offset); if (start < 0) return Unknown;
            var p = start; while (p < all.Count && !all[p].Is("(")) p++;
            var close = p; while (close < all.Count && !all[close].Is(")")) close++;
            var end = close; while (end < all.Count && !all[end].Is("КонецФункции")) end++;
            if (end == all.Count) return Unknown;
            var child = new Engine(schema, text, sources, module is null ? title : "", depth + 1, clock);
            var parameters = method.Parameters.Split(',');
            for (var n = 0; n < parameters.Length; n++)
            { var param = Regex.Match(parameters[n], @"(?:Знач\s+)?([\p{L}_][\p{L}\p{N}_]*)"); if (param.Success) child.Variables[param.Groups[1].Value] = args.ElementAtOrDefault(n) ?? Unknown; }
            child.Run(all.Skip(close + 1).Take(end - close - 1).Where(t => !t.Is("Экспорт")).ToList());
            var inferred = child.FinalReturn && child.Returns.Count > 0 ? child.Returns.Aggregate((a, b) => Merge(a, b)) : Unknown;
            return inferred.Kind != "unknown" ? inferred : DescribedReturn(text, method.Offset);
        }
        private Shape DescribedReturn(string text, int offset)
        {
            var start = text.LastIndexOf('\n', Math.Max(0, offset - 1)); var lines = new List<string>();
            var prefix = start < 0 ? "" : text[..start];
            foreach (var line in prefix.Split('\n').Reverse())
            { if (!line.TrimStart().StartsWith("//")) break; lines.Insert(0, line.TrimStart()[2..].Trim()); }
            var marker = lines.FindIndex(l => l.StartsWith("Возвращаемое значение", StringComparison.OrdinalIgnoreCase));
            if (marker < 0 || marker + 1 >= lines.Count) return Unknown;
            var type = lines[marker + 1].Split(" - ")[0].Trim();
            var result = schema.Type(type);
            var fields = result.Kind is "struct" or "table" ? result.Fields : null;
            if (fields is not null) foreach (var line in lines.Skip(marker + 2))
            { var m = Regex.Match(line, @"^\*\s*([\p{L}_][\p{L}\p{N}_]*)\s*-\s*([^\s-]+)"); if (m.Success) fields[m.Groups[1].Value] = schema.Type(m.Groups[2].Value); }
            return result;
        }
        internal IEnumerable<(string Name, Shape Type)> Members(Shape shape) => schema.Members(shape);
    }
    private static bool ValidName(string value) => Regex.IsMatch(value, @"^[\p{L}_][\p{L}\p{N}_]*$");
    private static string TypeCaption(Shape type)
    {
        var name = type.Path.Contains('.') ? type.Path[(type.Path.IndexOf('.') + 1)..] : type.Path;
        return type.Kind switch
        {
            "reference" => "Ссылка: " + name,
            "object" => "Объект: " + name,
            "manager" => "Менеджер: " + name,
            "enum" => "Перечисление: " + name,
            "struct" => "Структура",
            "table" => type.Path.Length > 0 ? "Табличная часть" : "Таблица значений",
            "row" => type.Path.Length > 0 ? "Строка табличной части" : "Строка таблицы",
            "unknown" => "Не определён",
            _ => type.Kind
        };
    }
    internal static CompletionChoice[] Find(EditorSnapshot snapshot, MetadataIndex[] sources)
    {
        try
        {
            var before = snapshot.Text[..snapshot.Offset]; var tokens = Lex(before);
            if (tokens.Count == 0) return [];
            var dot = snapshot.Prefix.Length > 0 ? tokens.Count - 2 : tokens.Count - 1;
            if (dot < 0 || !tokens[dot].Is(".")) return [];
            var methods = ModuleOutline.Parse(snapshot.Text); var owner = methods.LastOrDefault(m => m.Offset < snapshot.Offset);
            var scopeStart = 0;
            var inMethod = false;
            if (owner is not null)
            {
                var header = tokens.FindIndex(t => t.Position == owner.Offset);
                if (header >= 0)
                {
                    scopeStart = header; while (scopeStart < tokens.Count && !tokens[scopeStart].Is(")")) scopeStart++; scopeStart++;
                    if (scopeStart < tokens.Count && tokens[scopeStart].Is("Экспорт")) scopeStart++;
                    var end = tokens.FindIndex(Math.Min(scopeStart, tokens.Count), t => t.Is("КонецПроцедуры") || t.Is("КонецФункции"));
                    if (end >= 0) scopeStart = end + 1; else inMethod = true;
                }
            }
            var expressionStart = dot; var balance = 0;
            while (expressionStart > scopeStart)
            {
                var t = tokens[expressionStart - 1];
                if (t.Is(")") || t.Is("]")) balance++;
                if (t.Is("(") || t.Is("[")) { if (balance == 0) break; balance--; }
                if (balance == 0 && (t.Is(";") || t.Is("=") || t.Is("Тогда") || t.Is("Цикл") || t.Is("Возврат") || t.Is("Иначе"))) break;
                expressionStart--;
            }
            var engine = new Engine(GetSchema(sources), snapshot.Text, sources, snapshot.WindowTitle);
            if (inMethod && owner is not null)
                foreach (var parameter in owner.Parameters.Split(','))
                {
                    var name = Regex.Match(parameter, @"(?:Знач\s+)?([\p{L}_][\p{L}\p{N}_]*)");
                    if (name.Success) engine.Variables[name.Groups[1].Value] = Unknown;
                }
            engine.Run(tokens.Skip(scopeStart).Take(expressionStart - scopeStart).ToList());
            var position = expressionStart; var value = engine.Expression(tokens.Take(dot).ToList(), ref position);
            if (position != dot) return [];
            return engine.Members(value).Select(m => new CompletionChoice(m.Name + "  · " + TypeCaption(m.Type), snapshot.Prefix.Length, null, m.Name)
                { TypeText = TypeCaption(m.Type) }).ToArray();
        }
        catch (Exception ex) when (ex is TimeoutException or RegexMatchTimeoutException or IOException or UnauthorizedAccessException)
        { return []; }
    }
}

internal static class FunctionSources
{
    private static readonly Dictionary<string, (DateTime Stamp, long Length, string Text)> cache = new(StringComparer.OrdinalIgnoreCase);
    internal static string? Read(MetadataIndex[] sources, string module, string method)
    {
        if (!Regex.IsMatch(module, @"^[\p{L}_][\p{L}\p{N}_]*$")) return null;
        foreach (var source in sources.Reverse())
        {
            if (source.SourceKind != "xml-folder") continue;
            var path = ExportPaths.Child(source.Source, Path.Combine("CommonModules", module, "Ext", "Module.bsl"));
            var info = new FileInfo(path); if (!info.Exists || info.Length > 2_000_000) continue;
            lock (cache)
            {
                string text;
                if (cache.TryGetValue(path, out var old) && old.Stamp == info.LastWriteTimeUtc && old.Length == info.Length) text = old.Text;
                else { text = File.ReadAllText(path); if (cache.Count >= 32) cache.Clear(); cache[path] = (info.LastWriteTimeUtc, info.Length, text); }
                if (ModuleOutline.Parse(text).Any(m => m.Name.Equals(method, StringComparison.OrdinalIgnoreCase))) return text;
            }
        }
        return null;
    }
}
