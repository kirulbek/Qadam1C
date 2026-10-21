using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed record MetadataEntry(string Path, string Name, string Synonym, string[] Types, string[] Registers);
internal sealed record MetadataIndex(string Id, string Name, string Source, DateTime Updated, List<MetadataEntry> Entries)
{
    public override string ToString() => $"{Name} — {Entries.Count} объектов и полей — {Updated:g}";
}
internal sealed record ConfiguratorTarget(int Pid, long Started, long Window, string Title)
{
    public string Key => BaseIdentity.Get(Pid, Started);
    public override string ToString() => $"{Pid} — {Title}";
    public static ConfiguratorTarget[] List() => Process.GetProcessesByName("1cv8").Select(p =>
    {
        using (p)
        {
            try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains("Конфигуратор")
                ? new ConfiguratorTarget(p.Id, p.StartTime.ToUniversalTime().Ticks, p.MainWindowHandle.ToInt64(), p.MainWindowTitle) : null; }
            catch { return null; }
        }
    }).OfType<ConfiguratorTarget>().ToArray();
}

internal static class MetadataReport
{
    internal static readonly string[] Roots = ["Справочники", "Документы", "Перечисления", "Константы",
        "РегистрыСведений", "РегистрыНакопления", "РегистрыБухгалтерии", "РегистрыРасчета",
        "ПланыСчетов", "ПланыВидовХарактеристик", "ПланыВидовРасчета", "БизнесПроцессы", "Задачи",
        "ОбщиеМодули", "Отчеты", "Обработки"];
    public static MetadataIndex Read(string path, string? id = null, CancellationToken cancellation = default)
    {
        var entries = new List<MetadataEntry>();
        string? pending = null, heading = null, property = null;
        var props = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var registers = new HashSet<string>();
        bool supported = false;
        void Flush()
        {
            if (!supported || heading is null) return;
            string First(string key) => props.GetValueOrDefault(key)?.FirstOrDefault() ?? "";
            entries.Add(new(heading, First("Имя"), First("Синоним"), props.GetValueOrDefault("Тип")?.ToArray() ?? [], registers.ToArray()));
        }
        void Property(string cell)
        {
            if (!supported) return;
            var colon = cell.IndexOf(':');
            var value = cell;
            if (colon >= 0)
            {
                property = cell[..colon];
                if (property is "Имя" or "Синоним" or "Тип") props[property] = [];
                value = cell[(colon + 1)..].Trim();
            }
            if (value.Length > 0 && property is not null && props.TryGetValue(property, out var values))
                values.Add(value);
            if (value.StartsWith("РегистрНакопления.") || value.StartsWith("РегистрБухгалтерии.") || value.StartsWith("РегистрРасчета."))
                registers.Add(value);
        }
        foreach (var cell in Cells(path, cancellation))
        {
            if (pending is not null)
            {
                if (cell.StartsWith("Имя: ", StringComparison.Ordinal) && pending.Length < 2048 &&
                    Regex.IsMatch(pending, @"^[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)+$"))
                {
                    Flush();
                    if (heading is null && !pending.StartsWith("Конфигурации."))
                        throw new InvalidDataException("Отчёт должен содержать всю конфигурацию.");
                    heading = pending; props.Clear(); registers.Clear(); property = null;
                    supported = heading.StartsWith("Конфигурации.") || Roots.Any(r => heading.StartsWith(r + ".", StringComparison.Ordinal));
                }
                else Property(pending);
            }
            pending = cell;
        }
        if (pending is not null) Property(pending);
        Flush();
        if (entries.Count == 0) throw new InvalidDataException("В отчёте не найдена структура конфигурации.");
        if (entries.Any(e => string.IsNullOrEmpty(e.Name)) || entries.Select(e => e.Path).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Отчёт содержит неоднозначные или пустые имена.");
        return new(id ?? Guid.NewGuid().ToString("N"), entries[0].Name, Path.GetFullPath(path), DateTime.Now, entries);
    }

    // Read only text cells, retaining at most one cell and one object's properties.
    // Large reports contain formatting and descriptions which do not belong in the index.
    internal static IEnumerable<string> Cells(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var signature = new byte[6];
        if (stream.Read(signature) != 6 || Encoding.ASCII.GetString(signature) != "MOXCEL")
            throw new InvalidDataException("Ожидается табличный отчёт по конфигурации .mxl.");
        var first = -1;
        for (var i = 0; i < 4096 && (first = stream.ReadByte()) >= 0; i++) if (first == '{') break;
        if (first != '{') throw new InvalidDataException("Эта версия MXL пока не поддерживается.");
        stream.Position--;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 65536);
        const string marker = "{\"#\",\"";
        var matched = 0; var ticks = 0;
        while (reader.Read() is var raw && raw >= 0)
        {
            if ((++ticks & 65535) == 0) cancellation.ThrowIfCancellationRequested();
            var c = (char)raw;
            if (c != marker[matched]) { matched = c == marker[0] ? 1 : 0; continue; }
            if (++matched != marker.Length) continue;
            matched = 0;
            var value = new StringBuilder();
            var oversized = false;
            var closed = false;
            while ((raw = reader.Read()) >= 0)
            {
                if ((++ticks & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                c = (char)raw;
                if (c == '"')
                {
                    if (reader.Peek() == '"') reader.Read();
                    else { closed = true; break; }
                }
                if (value.Length < 65536) value.Append(c);
                else oversized = true;
            }
            if (!closed) throw new InvalidDataException("Отчёт обрезан внутри текстовой ячейки.");
            // MXL also uses the same prefix for non-cell structures with extra fields.
            if (reader.Read() != '}') continue;
            yield return oversized ? "" : value.ToString();
        }
        cancellation.ThrowIfCancellationRequested();
    }
}

internal sealed class MetadataStore
{
    internal sealed record State(List<MetadataIndex> Caches, Dictionary<string, string> Bindings);
    private readonly string path;
    private State state;
    public IReadOnlyList<MetadataIndex> Caches => state.Caches;
    public MetadataStore(string path)
    {
        this.path = path;
        state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Не удалось прочитать кэш метаданных.") : new([], []);
    }
    public MetadataIndex? For(string session) => state.Bindings.TryGetValue(session, out var id) ? state.Caches.Find(c => c.Id == id) : null;
    private void Save(State value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
        state = value;
    }
    public void Put(MetadataIndex index, string session)
    {
        var caches = state.Caches.Where(c => c.Id != index.Id).Append(index).ToList();
        var bindings = new Dictionary<string, string>(state.Bindings) { [session] = index.Id };
        Save(new(caches, bindings));
    }
    public void Bind(string session, string id)
    {
        if (!state.Caches.Any(c => c.Id == id)) throw new InvalidOperationException("Кэш не найден.");
        Save(new(state.Caches, new(state.Bindings) { [session] = id }));
    }
    public void Unbind(string session)
    {
        var bindings = new Dictionary<string, string>(state.Bindings);
        bindings.Remove(session);
        Save(new(state.Caches, bindings));
    }
}

internal static class MetadataCompletion
{
    private static readonly Dictionary<string, string> TypeRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Справочник"] = "Справочники", ["Документ"] = "Документы", ["Перечисление"] = "Перечисления",
        ["РегистрСведений"] = "РегистрыСведений", ["РегистрНакопления"] = "РегистрыНакопления",
        ["РегистрБухгалтерии"] = "РегистрыБухгалтерии", ["РегистрРасчета"] = "РегистрыРасчета",
        ["ПланСчетов"] = "ПланыСчетов", ["ПланВидовХарактеристик"] = "ПланыВидовХарактеристик",
        ["БизнесПроцесс"] = "БизнесПроцессы", ["Задача"] = "Задачи"
    };
    private static string? TypePath(string type)
    {
        var dot = type.IndexOf('.');
        if (dot < 0) return null;
        var kind = type[..dot].Replace("Ссылка", "").Replace("Объект", "");
        return TypeRoots.TryGetValue(kind, out var root) ? root + type[dot..] : null;
    }
    // Preserve offsets while hiding comments and string literals from context matching.
    internal static string Code(string text)
    {
        var b = text.ToCharArray(); bool quoted = false, comment = false;
        for (var i = 0; i < b.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n') { comment = false; continue; }
            if (comment) { b[i] = ' '; continue; }
            if (!quoted && c == '/' && i + 1 < b.Length && text[i + 1] == '/') { comment = true; b[i] = ' '; continue; }
            if (c == '"')
            {
                b[i] = ' ';
                if (quoted && i + 1 < b.Length && text[i + 1] == '"') { b[++i] = ' '; continue; }
                quoted = !quoted; continue;
            }
            if (quoted) b[i] = ' ';
        }
        return new(b);
    }
    private static string? CurrentObject(string title, MetadataIndex cache)
    {
        var m = Regex.Match(title, @"^(Документ|Справочник|РегистрСведений|РегистрНакопления|БизнесПроцесс|Задача)\s+([\p{L}\p{N}_]+):", RegexOptions.IgnoreCase);
        if (!m.Success || !TypeRoots.TryGetValue(m.Groups[1].Value, out var root)) return null;
        var path = root + "." + m.Groups[2].Value;
        return cache.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ? path : null;
    }
    public static CompletionChoice[]? Find(EditorSnapshot snapshot, MetadataIndex? cache)
    {
        if (cache is null) return null;
        var before = snapshot.Text[..snapshot.Offset];
        var code = Code(before);
        var match = Regex.Match(code, @"(?<chain>[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)*)\.(?<prefix>[\p{L}\p{N}_]*)$");
        if (!match.Success) return null;
        var prefix = match.Groups["prefix"].Value;
        var chain = match.Groups["chain"].Value;
        var current = CurrentObject(snapshot.WindowTitle, cache);
        var entries = cache.Entries;
        IEnumerable<(string Name, string Kind)> candidates = [];
        if (MetadataReport.Roots.Contains(chain, StringComparer.OrdinalIgnoreCase))
            candidates = entries.Where(e => e.Path.StartsWith(chain + ".", StringComparison.OrdinalIgnoreCase) && e.Path.Split('.').Length == 2).Select(e => (e.Name, e.Synonym));
        else
        {
            string? Resolve(string expression, int depth = 0)
            {
                if (depth > 4) return null;
                if (expression.Equals("Объект", StringComparison.OrdinalIgnoreCase) ||
                    expression.Equals("ЭтотОбъект", StringComparison.OrdinalIgnoreCase)) return current;
                if (expression.Equals("Движения", StringComparison.OrdinalIgnoreCase)) return current is null ? null : "@movements";
                if (expression.StartsWith("Движения.", StringComparison.OrdinalIgnoreCase) && expression.Split('.').Length == 2)
                {
                    var name = expression.Split('.')[1];
                    var owner = entries.FirstOrDefault(e => e.Path == current);
                    var register = owner?.Registers.FirstOrDefault(r => r.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
                    return register is null ? null : TypePath(register);
                }
                // Explicit metadata paths are managers, handled separately below.
                var dot = expression.LastIndexOf('.');
                if (dot > 0)
                {
                    var parent = Resolve(expression[..dot], depth + 1);
                    if (parent is null) return null;
                    var member = expression[(dot + 1)..];
                    var section = entries.FirstOrDefault(e => e.Path.Equals(parent + ".ТабличныеЧасти." + member, StringComparison.OrdinalIgnoreCase));
                    if (section is not null) return section.Path;
                    var field = entries.FirstOrDefault(e => e.Path.Equals(parent + ".Реквизиты." + member, StringComparison.OrdinalIgnoreCase));
                    var paths = field?.Types.Select(TypePath).OfType<string>().Distinct().ToArray();
                    return paths?.Length == 1 ? paths[0] : null;
                }
                // Only infer simple assignments within the current procedure.
                var start = Regex.Matches(code[..match.Index], @"(?im)^\s*(?:Процедура|Функция)\s+").LastOrDefault()?.Index ?? 0;
                var assignments = Regex.Matches(code[start..match.Index], @"(?im)(?<![\p{L}\p{N}_.])" + Regex.Escape(expression) + @"\s*=\s*([^;\r\n]+)");
                if (assignments.Count == 0)
                {
                    var field = entries.FirstOrDefault(e => e.Path.Equals(current + ".Реквизиты." + expression, StringComparison.OrdinalIgnoreCase));
                    return field?.Types.Length == 1 ? TypePath(field.Types[0]) : null;
                }
                var rhs = assignments[^1].Groups[1].Value.Trim();
                var factory = Regex.Match(rhs, @"^(Справочники|Документы)\.([\p{L}\p{N}_]+)\.(СоздатьЭлемент|СоздатьДокумент|НайтиПоКоду|НайтиПоНаименованию|НайтиПоНомеру|ПолучитьСсылку)\([^()]*\)$", RegexOptions.IgnoreCase);
                if (factory.Success) return factory.Groups[1].Value + "." + factory.Groups[2].Value;
                var add = Regex.Match(rhs, @"^([\p{L}\p{N}_.]+)\.Добавить\(\)$", RegexOptions.IgnoreCase);
                if (add.Success) return Resolve(add.Groups[1].Value, depth + 1);
                return Regex.IsMatch(rhs, @"^[\p{L}\p{N}_.]+$") ? Resolve(rhs, depth + 1) : null;
            }
            if (chain.StartsWith("Перечисления.", StringComparison.OrdinalIgnoreCase))
                candidates = entries.Where(e => e.Path.StartsWith(chain + ".ЗначенияПеречисления.", StringComparison.OrdinalIgnoreCase)).Select(e => (e.Name, "значение"));
            else
            {
                var path = Resolve(chain);
                if (path == "@movements")
                    candidates = entries.First(e => e.Path == current).Registers.Select(r => (r.Split('.')[1], "движения"));
                else if (path is not null)
                {
                    candidates = entries.Where(e => new[] { ".Реквизиты.", ".Измерения.", ".Ресурсы.", ".ТабличныеЧасти." }
                        .Any(part => e.Path.StartsWith(path + part, StringComparison.OrdinalIgnoreCase) &&
                            !e.Path[(path.Length + part.Length)..].Contains('.')))
                        .Select(e => (e.Name, string.Join(", ", e.Types)));
                }
            }
        }
        return candidates.Where(c => SmartCompletion.MatchScore(c.Name, prefix) >= 0)
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Name)
            .Select(c => new CompletionChoice(c.Name + (c.Kind.Length > 0 ? "  · " + c.Kind : ""), prefix.Length, null, c.Name)).ToArray();
    }
}
