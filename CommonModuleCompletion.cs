using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Qadam1C;

internal static class CommonModuleCompletion
{
    private sealed record Cache(DateTime Stamp, long Length, Dictionary<string, IndexedModule> Modules);
    private static readonly Dictionary<string, Cache> caches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object gate = new();

    // Called on the completion worker. Retain only common modules, not every object's methods.
    private static Dictionary<string, IndexedModule> Read(string source)
    {
        var path = Path.Combine(source, ".qadam-modules.json");
        lock (gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new(StringComparer.OrdinalIgnoreCase);
                if (caches.TryGetValue(path, out var previous) && previous.Stamp == info.LastWriteTimeUtc && previous.Length == info.Length)
                    return previous.Modules;
                Dictionary<string, IndexedModule> modules;
                try { modules = Load(path).GetAwaiter().GetResult(); }
                catch (JsonException) { modules = new(StringComparer.OrdinalIgnoreCase); }
                if (caches.Count >= 8) caches.Clear();
                caches[path] = new(info.LastWriteTimeUtc, info.Length, modules);
                return modules;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { return new(StringComparer.OrdinalIgnoreCase); }
        }
    }
    private static async Task<Dictionary<string, IndexedModule>> Load(string path)
    {
        var result = new Dictionary<string, IndexedModule>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await foreach (var module in JsonSerializer.DeserializeAsyncEnumerable<IndexedModule>(stream).ConfigureAwait(false))
        {
            if (module?.File is null || module.Methods is null) continue;
            var parts = module.File.Replace('\\', '/').Split('/');
            if (parts.Length != 4 || !parts[0].Equals("CommonModules", StringComparison.OrdinalIgnoreCase) ||
                !parts[2].Equals("Ext", StringComparison.OrdinalIgnoreCase) || !parts[3].Equals("Module.bsl", StringComparison.OrdinalIgnoreCase)) continue;
            result[parts[1]] = module with { Methods = module.Methods.Where(m => m is not null && m.Exported &&
                Regex.IsMatch(m.Name ?? "", @"^[\p{L}_][\p{L}\p{N}_]*$")).ToArray() };
        }
        return result;
    }
    internal static CompletionChoice[] Find(EditorSnapshot snapshot, IEnumerable<MetadataIndex> sources)
    {
        var code = MetadataCompletion.Code(snapshot.Text[..snapshot.Offset]);
        var match = Regex.Match(code, @"(?<![\p{L}\p{N}_.])(?<module>[\p{L}_][\p{L}\p{N}_]*)\.(?<prefix>[\p{L}\p{N}_]*)$");
        if (!match.Success) return [];
        var name = match.Groups["module"].Value;
        var scopeStart = Regex.Matches(code[..match.Index], @"(?im)^\s*(?:Процедура|Функция|Procedure|Function)\s+").LastOrDefault()?.Index ?? 0;
        var scope = code[scopeStart..match.Index];
        var escaped = Regex.Escape(name);
        if (Regex.IsMatch(scope, @"(?<![\p{L}\p{N}_.])" + escaped + @"\s*=(?!=)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(scope, @"(?im)^\s*(?:Перем|Var)\s+[^;\r\n]*\b" + escaped + @"\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(scope, @"(?is)^\s*(?:Процедура|Функция|Procedure|Function)\s+[\p{L}\p{N}_]+\s*\([^)]*\b" + escaped + @"\b", RegexOptions.IgnoreCase)) return [];
        var methods = new Dictionary<string, (ModuleMethod Method, MethodDefinition Definition)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source.SourceKind != "xml-folder" || string.IsNullOrWhiteSpace(source.Source)) continue;
            if (!Read(source.Source).TryGetValue(name, out var found)) continue;
            foreach (var method in found.Methods)
                methods[method.Name] = (method, new(source.Source, found.File, found.Sha256, name, method.Name, source.Name, source.Updated));
        }
        var opening = snapshot.Text[snapshot.Offset..].TrimStart().StartsWith('(');
        return methods.Values.Select(value =>
        {
            var m = value.Method;
            var insert = opening ? m.Name : m.Name + "()";
            return new CompletionChoice(m.Name + "(" + m.Parameters + ")  · " + m.Kind,
                snapshot.Prefix.Length, null, insert,
                opening || string.IsNullOrWhiteSpace(m.Parameters) ? insert.Length : insert.Length - 1, m.Name)
                { Definition = value.Definition };
        }).ToArray();
    }
}

internal static class ExtensionCaption
{
    private static readonly Dictionary<string, (DateTime Stamp, string[] Names)> cache = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Matches(MetadataIndex index, string caption)
    {
        if (string.Equals(index.ExtensionName, caption, StringComparison.OrdinalIgnoreCase)) return true;
        if (index.ExtensionName is null || index.SourceKind != "xml-folder") return false;
        try
        {
            var path = Path.Combine(index.Source, "Configuration.xml");
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!cache.TryGetValue(path, out var item) || item.Stamp != stamp)
            {
                using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
                var config = XDocument.Load(reader).Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Configuration");
                var properties = config?.Elements().SingleOrDefault(e => e.Name.LocalName == "Properties");
                var names = properties?.Elements().Where(e => e.Name.LocalName == "Synonym")
                    .SelectMany(e => e.Descendants().Where(n => n.Name.LocalName == "content")).Select(e => e.Value).ToArray() ?? [];
                if (cache.Count >= 32) cache.Clear();
                cache[path] = item = (stamp, names);
            }
            return item.Names.Contains(caption, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        { return false; }
    }
}
