using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Qadam.MetadataProbe;

record Entry(string Id, string Name, string Version);
record Snapshot(string BaseId, string ConfigurationId, string Format, string ExportVersion,
    string SourceHash, Entry[] Entries);
record Change(string Kind, string Id, string? Before, string? After);

static class ManifestReader
{
    const string Namespace = "http://v8.1c.ru/8.3/xcf/dumpinfo";

    public static Snapshot Read(string path, string baseId) => Parse(File.ReadAllBytes(path), baseId);

    public static Snapshot Parse(byte[] bytes, string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId)) throw new InvalidDataException("Не задан идентификатор базы.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Нет корня XML.");
        XNamespace ns = Namespace;
        if (root.Name != ns + "ConfigDumpInfo") throw new InvalidDataException("Неизвестный формат ConfigDumpInfo.");
        string Attr(XElement element, string name) => (string?)element.Attribute(name) is { Length: > 0 } value
            ? value : throw new InvalidDataException($"Нет атрибута {name}.");
        var format = Attr(root, "format");
        if (format != "Hierarchical") throw new InvalidDataException("Прототип поддерживает только Hierarchical.");
        var exportVersion = Attr(root, "version");
        var versions = root.Element(ns + "ConfigVersions") ?? throw new InvalidDataException("Нет ConfigVersions.");
        var entries = versions.Descendants(ns + "Metadata").Select(e => new Entry(
            Attr(e, "id"), Attr(e, "name"), (string?)e.Attribute("configVersion") ?? ""))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        if (entries.Length == 0) throw new InvalidDataException("Пустой список метаданных.");
        if (entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("Повторяются идентификаторы метаданных.");
        if (entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("Повторяются имена метаданных.");
        var configurations = entries.Where(e => e.Name.StartsWith("Configuration.", StringComparison.Ordinal)
            && e.Name.Count(c => c == '.') == 1).ToArray();
        if (configurations.Length != 1) throw new InvalidDataException("Не удалось однозначно определить конфигурацию.");
        return new(baseId, configurations[0].Id, format, exportVersion,
            Convert.ToHexString(SHA256.HashData(bytes)), entries);
    }

    public static Change[] Compare(Snapshot before, Snapshot after)
    {
        if (before.BaseId != after.BaseId || before.ConfigurationId != after.ConfigurationId)
            throw new InvalidDataException("Нельзя смешивать базы, конфигурации или расширения.");
        if (before.Format != after.Format || before.ExportVersion != after.ExportVersion)
            throw new InvalidDataException("Изменился формат выгрузки; требуется новый исходный снимок.");
        var old = before.Entries.ToDictionary(e => e.Id);
        var current = after.Entries.ToDictionary(e => e.Id);
        var changes = new List<Change>();
        foreach (var entry in after.Entries)
        {
            if (!old.TryGetValue(entry.Id, out var previous)) changes.Add(new("добавлен", entry.Id, null, entry.Name));
            else
            {
                if (previous.Name != entry.Name) changes.Add(new("переименован", entry.Id, previous.Name, entry.Name));
                if (previous.Version != entry.Version) changes.Add(new("версия изменена", entry.Id, previous.Name, entry.Name));
            }
        }
        foreach (var entry in before.Entries)
            if (!current.ContainsKey(entry.Id)) changes.Add(new("удалён", entry.Id, entry.Name, null));
        return changes.ToArray();
    }
}

static class Checks
{
    public static void Run()
    {
        const string header = "<ConfigDumpInfo xmlns='http://v8.1c.ru/8.3/xcf/dumpinfo' format='Hierarchical' version='2.18'><ConfigVersions>";
        const string footer = "</ConfigVersions></ConfigDumpInfo>";
        const string root = "<Metadata name='Configuration.Тест' id='root' configVersion='v1'/>";
        string Xml(string body) => header + root + body + footer;
        Snapshot Parse(string xml, string scope = "тестовая-база") => ManifestReader.Parse(Encoding.UTF8.GetBytes(xml), scope);
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or XmlException)
            { Check(true, name); return; }
            throw new Exception("Ожидался отказ: " + name);
        }
        var original = Parse(Xml("<Metadata name='Catalog.Товары' id='catalog' configVersion='v1'><Metadata name='Catalog.Товары.Attribute.Код' id='field'/></Metadata>"));
        Check(original.Entries.Length == 3, "вложенные метаданные и кириллица");
        Check(ManifestReader.Compare(original, original).Length == 0, "неизменённый снимок");
        var renamed = Parse(Xml("<Metadata name='Catalog.Номенклатура' id='catalog' configVersion='v2'/><Metadata name='CommonModule.Тест' id='module' configVersion='v1'/>"));
        var delta = ManifestReader.Compare(original, renamed);
        Check(delta.Count(c => c.Kind == "переименован") == 1, "переименование по стабильному ID");
        Check(delta.Count(c => c.Kind == "версия изменена") == 1, "изменение версии");
        Check(delta.Count(c => c.Kind == "добавлен") == 1 && delta.Count(c => c.Kind == "удалён") == 1, "добавление и удаление");
        Reject(() => ManifestReader.Compare(original, Parse(Xml(""), "другая-база")), "изоляция баз с одинаковым UUID конфигурации");
        Reject(() => ManifestReader.Compare(original, Parse(Xml("").Replace("id='root'", "id='extension'"))), "изоляция расширения");
        Reject(() => ManifestReader.Compare(original, Parse(Xml("").Replace("2.18", "2.19"))), "смена формата выгрузки");
        Reject(() => Parse(Xml("<Metadata id='root' name='Catalog.Дубликат'/>")), "дубликат ID");
        Reject(() => Parse(Xml("<Metadata name='Catalog.НетID'/>")), "неполная запись");
        Reject(() => Parse(Xml("").Replace("</ConfigVersions>", "")), "оборванный XML");
        Reject(() => Parse(Xml("").Replace("http://v8.1c.ru/8.3/xcf/dumpinfo", "urn:unknown")), "неизвестное пространство имён");
        Reject(() => Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]>" + Xml("")), "запрет внешних сущностей");
        Console.WriteLine($"Проверок пройдено: {passed}");
    }
}

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            if (args is ["self-test"]) { Checks.Run(); return 0; }
            if (args is ["inspect", var file, var baseId])
                Console.WriteLine(JsonSerializer.Serialize(ManifestReader.Read(file, baseId), options));
            else if (args is ["compare", var oldFile, var newFile, var scope])
                Console.WriteLine(JsonSerializer.Serialize(ManifestReader.Compare(ManifestReader.Read(oldFile, scope), ManifestReader.Read(newFile, scope)), options));
            else
            {
                Console.Error.WriteLine("Команды: self-test | inspect <ConfigDumpInfo.xml> <ID-базы> | compare <старый.xml> <новый.xml> <ID-базы>");
                return 2;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
