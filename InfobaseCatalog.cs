using System.IO;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed record Infobase(string Name, string Option, string Address, string Key, string Version)
{
    public override string ToString() => $"{Name} — {Address}";
}

internal static class InfobaseCatalog
{
    internal static bool VersionMatches(string installed, string requested)
    {
        var prefix = requested.Trim().TrimEnd('*').TrimEnd('.');
        return prefix.Length == 0 || installed.Equals(prefix, StringComparison.Ordinal) ||
            installed.StartsWith(prefix + ".", StringComparison.Ordinal);
    }
    internal static IEnumerable<Dictionary<string, string>> Sections(string text)
    {
        Dictionary<string, string>? row = null;
        foreach (var line in text.Split('\n').Select(s => s.Trim().TrimStart('\uFEFF')))
        {
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (row is not null) yield return row;
                row = new(StringComparer.OrdinalIgnoreCase) { ["Name"] = line[1..^1] };
            }
            else if (row is not null && line.IndexOf('=') is var p && p > 0)
                row[line[..p].Trim()] = line[(p + 1)..].Trim();
        }
        if (row is not null) yield return row;
    }
    internal static Infobase? Parse(Dictionary<string, string> row)
    {
        var connection = row.GetValueOrDefault("Connect", "");
        string? Value(string key)
        {
            var m = Regex.Match(connection, @"(?:^|;)\s*" + key + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^;]*))", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.Trim() : null;
        }
        var file = Value("File"); var server = Value("Srvr"); var reference = Value("Ref");
        string option, address;
        if (file is not null && Path.IsPathFullyQualified(file)) { option = "/F"; address = Path.GetFullPath(file); }
        else if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(reference))
        { option = "/S"; address = server + "\\" + reference; }
        else return null; // Folders and web-only connections cannot run Designer.
        if (address.Contains('"') || address.Contains('\r') || address.Contains('\n')) return null;
        var key = BaseIdentity.FromCommandLine("1cv8.exe DESIGNER " + option + " \"" + address.TrimEnd('\\') + "\"");
        if (key is null) return null;
        return new(row.GetValueOrDefault("Name", "База 1С"), option, address, key,
            row.GetValueOrDefault("DefaultVersion", row.GetValueOrDefault("Version", "")));
    }
    internal static Infobase[] Load(out string[] warnings)
    {
        var result = new List<Infobase>(); var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string file, int depth = 0)
        {
            if (depth > 8) { issues.Add("Слишком много вложенных общих списков."); return; }
            file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(file) || !seen.Add(file)) return;
            try
            {
                if (!File.Exists(file)) return;
                var text = File.ReadAllText(file);
                if (Path.GetExtension(file).Equals(".v8i", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var row in Sections(text)) { var b = Parse(row); if (b is not null) result.Add(b); }
                }
                else foreach (var line in text.Split('\n'))
                {
                    var p = line.IndexOf('='); if (p < 1) continue;
                    var key = line[..p].Trim();
                    if (key is "CommonInfoBases" or "CommonCfgLocation")
                    {
                        var path = Environment.ExpandEnvironmentVariables(line[(p + 1)..].Trim().Trim('"'));
                        Visit(Path.IsPathFullyQualified(path) ? path : Path.Combine(Path.GetDirectoryName(file)!, path), depth + 1);
                    }
                    else if (key == "WebCommonInfoBases") issues.Add("Списки баз из веб-сервисов пока не загружаются.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { issues.Add("Не удалось прочитать один из списков баз: " + file); }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart");
        Visit(Path.Combine(local, "ibases.v8i")); Visit(Path.Combine(local, "1CEStart.cfg"));
        Visit(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1C", "1CEStart", "1CEStart.cfg"));
        warnings = issues.Distinct().ToArray();
        return result.DistinctBy(b => b.Key).OrderBy(b => b.Name).ToArray();
    }
    internal static string[] Platforms()
    {
        var roots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "1cv8"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "1cv8_x86") };
        return roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateDirectories(r))
            .Select(d => Path.Combine(d, "bin", "1cv8.exe")).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => System.Version.TryParse(new DirectoryInfo(Path.GetDirectoryName(p)!).Parent!.Name, out var v) ? v : new System.Version()).ToArray();
    }
}
