using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Qadam1C;

// Imports an existing manual export. Never starts 1C or writes into the export folder.
internal static class ConfigurationFolder
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["Configuration"] = "Конфигурации", ["Catalog"] = "Справочники", ["Document"] = "Документы",
        ["CommonModule"] = "ОбщиеМодули", ["Enum"] = "Перечисления", ["EnumValue"] = "ЗначенияПеречисления",
        ["Constant"] = "Константы", ["InformationRegister"] = "РегистрыСведений",
        ["AccumulationRegister"] = "РегистрыНакопления", ["AccountingRegister"] = "РегистрыБухгалтерии",
        ["CalculationRegister"] = "РегистрыРасчета", ["ChartOfAccounts"] = "ПланыСчетов",
        ["ChartOfCharacteristicTypes"] = "ПланыВидовХарактеристик", ["ChartOfCalculationTypes"] = "ПланыВидовРасчета",
        ["BusinessProcess"] = "БизнесПроцессы", ["Task"] = "Задачи", ["Report"] = "Отчеты",
        ["DataProcessor"] = "Обработки", ["Attribute"] = "Реквизиты", ["TabularSection"] = "ТабличныеЧасти",
        ["Dimension"] = "Измерения", ["Resource"] = "Ресурсы", ["Form"] = "Формы", ["Command"] = "Команды"
    };
    public static MetadataIndex Read(string folder, MetadataIndex? previous, CancellationToken token, bool allowExtension = false)
    {
        folder = Path.GetFullPath(folder);
        XDocument ReadXml(string file)
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        token.ThrowIfCancellationRequested();
        var manifest = Path.Combine(folder, "ConfigDumpInfo.xml");
        if (!File.Exists(manifest) || !File.Exists(Path.Combine(folder, "Configuration.xml")))
            throw new InvalidDataException("Выберите корневую папку выгрузки: в ней должны находиться Configuration.xml и ConfigDumpInfo.xml. Сначала завершите выгрузку в 1С.");
        var before = File.ReadAllBytes(manifest);
        var root = ReadXml(manifest).Root;
        if (root?.Name != XName.Get("ConfigDumpInfo", "http://v8.1c.ru/8.3/xcf/dumpinfo") ||
            (string?)root.Attribute("format") != "Hierarchical")
            throw new InvalidDataException("Нужна иерархическая выгрузка конфигурации. Выберите этот формат при выгрузке в 1С.");
        var config = ReadXml(Path.Combine(folder, "Configuration.xml")).Root?.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "Configuration")
            ?? throw new InvalidDataException("Не удалось прочитать описание конфигурации.");
        var props = config.Elements().SingleOrDefault(e => e.Name.LocalName == "Properties");
        var name = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        var identity = (string?)config.Attribute("uuid");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException("В Configuration.xml отсутствуют имя или идентификатор конфигурации.");
        var isExtension = props!.Elements().Any(e => e.Name.LocalName == "ConfigurationExtensionPurpose" && e.Value.Length > 0);
        if (isExtension && !allowExtension)
            throw new InvalidDataException("Это выгрузка расширения. Для подключения основной конфигурации вместе с расширениями используйте «Подключить базы / обновить выгрузку».");
        if (previous?.ConfigurationId is { } old && old != identity)
            throw new InvalidDataException("В папке теперь другая конфигурация. Добавьте её отдельно; прежний список сохранён.");
        var entries = new List<MetadataEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool configurationFound = false;
        foreach (var item in root.Descendants(root.Name.Namespace + "Metadata"))
        {
            token.ThrowIfCancellationRequested();
            var fullName = (string?)item.Attribute("name") ?? throw new InvalidDataException("Выгрузка содержит объект без имени.");
            if (!seen.Add(fullName)) throw new InvalidDataException("В выгрузке повторяются объекты. Завершите выгрузку и повторите чтение.");
            var parts = fullName.Split('.');
            if (parts.Length == 2 && parts[0] == "Configuration")
            {
                if (parts[1] != name || (string?)item.Attribute("id") != identity)
                    throw new InvalidDataException("Файлы выгрузки относятся к разным конфигурациям. Выполните полную выгрузку в отдельную папку.");
                configurationFound = true;
            }
            // External properties (ObjectModule, Help, etc.) are not metadata fields.
            if (parts.Length % 2 != 0) continue;
            var translated = new List<string>();
            for (var i = 0; i < parts.Length; i += 2)
            {
                if (!Names.TryGetValue(parts[i], out var kind)) { translated.Clear(); break; }
                translated.Add(kind); translated.Add(parts[i + 1]);
            }
            if (translated.Count > 0) entries.Add(new(string.Join('.', translated), parts[^1], "", [], []));
        }
        if (!configurationFound) throw new InvalidDataException("Реестр выгрузки не содержит основную конфигурацию.");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.bsl", new EnumerationOptions {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        { token.ThrowIfCancellationRequested(); count++; }
        if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(manifest)))
            throw new IOException("Выгрузка изменяется. Дождитесь её завершения и повторите чтение.");
        return MetadataTypes.Enrich(new(previous?.Id ?? Guid.NewGuid().ToString("N"), previous?.Name ?? name, folder, DateTime.Now, entries)
        { ConfigurationId = identity, SourceKind = "xml-folder", ModuleCount = count,
            ExtensionName = isExtension ? name : null, BaseKey = previous?.BaseKey }, token);
    }
}
