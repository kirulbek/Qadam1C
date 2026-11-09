using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Qadam1C;

internal static class MetadataTypes
{
    internal static readonly Dictionary<string, string> Folders = new()
    {
        ["Справочники"] = "Catalogs", ["Документы"] = "Documents", ["Перечисления"] = "Enums",
        ["РегистрыСведений"] = "InformationRegisters", ["РегистрыНакопления"] = "AccumulationRegisters",
        ["РегистрыБухгалтерии"] = "AccountingRegisters", ["РегистрыРасчета"] = "CalculationRegisters",
        ["ПланыСчетов"] = "ChartsOfAccounts", ["ПланыВидовХарактеристик"] = "ChartsOfCharacteristicTypes",
        ["ПланыВидовРасчета"] = "ChartsOfCalculationTypes", ["БизнесПроцессы"] = "BusinessProcesses", ["Задачи"] = "Tasks"
    };
    internal static string TypeName(string value)
    {
        var local = value[(value.IndexOf(':') + 1)..];
        var dot = local.IndexOf('.');
        var kind = dot < 0 ? local : local[..dot];
        var mapped = kind switch
        {
            "CatalogRef" => "СправочникСсылка", "DocumentRef" => "ДокументСсылка", "EnumRef" => "ПеречислениеСсылка",
            "ChartOfAccountsRef" => "ПланСчетовСсылка", "ChartOfCharacteristicTypesRef" => "ПланВидовХарактеристикСсылка",
            "ChartOfCalculationTypesRef" => "ПланВидовРасчетаСсылка", "BusinessProcessRef" => "БизнесПроцессСсылка",
            "TaskRef" => "ЗадачаСсылка", "string" => "Строка", "decimal" => "Число", "boolean" => "Булево", "dateTime" => "Дата", _ => kind
        };
        return mapped + (dot < 0 ? "" : local[dot..]);
    }
    private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    internal static MetadataIndex Enrich(MetadataIndex index, CancellationToken token)
    {
        var entries = index.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Entries.Where(e => e.Path.Split('.').Length == 2).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var parts = entry.Path.Split('.');
            if (!Folders.TryGetValue(parts[0], out var folder)) continue;
            var file = ExportPaths.Child(index.Source, Path.Combine(folder, parts[1] + ".xml"));
            if (!File.Exists(file)) continue;
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
            var node = XDocument.Load(reader).Root?.Elements().FirstOrDefault();
            var properties = Child(node, "Properties");
            if (Child(properties, "Name")?.Value != parts[1]) throw new InvalidDataException("Имя XML-объекта не совпадает с индексом: " + entry.Path);
            string Property(string name) => Child(properties, name)?.Value ?? "";
            void Standard(string name, params string[] types)
            {
                var path = entry.Path + ".Реквизиты." + name;
                if (!entries.ContainsKey(path)) entries[path] = new(path, name, "", types, []);
            }
            // Borrowed extension objects may omit properties. Their main-configuration fields are inherited later.
            if (index.ExtensionName is null && parts[0] is "Документы" or "Справочники")
            {
                Standard("Ссылка", (parts[0] == "Документы" ? "ДокументСсылка." : "СправочникСсылка.") + parts[1]);
                Standard("ПометкаУдаления", "Булево");
                if (parts[0] == "Документы")
                {
                    Standard("Дата", "Дата");
                    if (int.TryParse(Property("NumberLength"), out var length) && length > 0) Standard("Номер", Property("NumberType") == "Number" ? "Число" : "Строка");
                    if (Property("Posting") == "Allow") Standard("Проведен", "Булево");
                }
                else
                {
                    if (int.TryParse(Property("CodeLength"), out var length) && length > 0) Standard("Код", Property("CodeType") == "Number" ? "Число" : "Строка");
                    if (int.TryParse(Property("DescriptionLength"), out var description) && description > 0) Standard("Наименование", "Строка");
                    if (Property("Hierarchical") == "true") Standard("Родитель", "СправочникСсылка." + parts[1]);
                    if (Property("Hierarchical") == "true" && Property("HierarchyType") == "HierarchyFoldersAndItems") Standard("ЭтоГруппа", "Булево");
                }
            }
            void Visit(XElement? owner, string parent)
            {
                foreach (var member in Child(owner, "ChildObjects")?.Elements() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    var group = member.Name.LocalName switch { "Attribute" => "Реквизиты", "TabularSection" => "ТабличныеЧасти",
                        "Dimension" => "Измерения", "Resource" => "Ресурсы", _ => null };
                    if (group is null) continue;
                    var props = Child(member, "Properties"); var name = Child(props, "Name")?.Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    var path = parent + "." + group + "." + name;
                    var types = Child(props, "Type")?.Elements().Where(e => e.Name.LocalName == "Type").Select(e => TypeName(e.Value)).Distinct().ToArray() ?? [];
                    var synonym = Child(props, "Synonym")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "content")?.Value ?? "";
                    entries[path] = new(path, name, synonym, types, []);
                    if (group == "ТабличныеЧасти")
                    {
                        var number = path + ".Реквизиты.НомерСтроки";
                        entries[number] = new(number, "НомерСтроки", "", ["Число"], []);
                        Visit(member, path);
                    }
                }
            }
            Visit(node, entry.Path);
        }
        return index with { Entries = entries.Values.ToList(), TypeSchemaVersion = 1 };
    }
}
