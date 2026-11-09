using System.IO;

namespace Qadam1C;

internal static class TypedCompletionChecks
{
    internal static int Run(string? livePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "qadam-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Documents")); Directory.CreateDirectory(Path.Combine(root, "Catalogs"));
            File.WriteAllText(Path.Combine(root, "Documents", "Заказ.xml"), "<MetaDataObject><Document><Properties><Name>Заказ</Name><NumberLength>11</NumberLength><NumberType>String</NumberType><Posting>Allow</Posting></Properties><ChildObjects><Attribute><Properties><Name>Партнер</Name><Type><Type>cfg:CatalogRef.Партнеры</Type></Type></Properties></Attribute><Attribute><Properties><Name>Неоднозначный</Name><Type><Type>cfg:CatalogRef.Партнеры</Type><Type>xs:string</Type></Type></Properties></Attribute><TabularSection><Properties><Name>Товары</Name></Properties><ChildObjects><Attribute><Properties><Name>Цена</Name><Type><Type>xs:decimal</Type></Type></Properties></Attribute></ChildObjects></TabularSection></ChildObjects></Document></MetaDataObject>");
            File.WriteAllText(Path.Combine(root, "Catalogs", "Партнеры.xml"), "<MetaDataObject><Catalog><Properties><Name>Партнеры</Name><CodeLength>0</CodeLength><DescriptionLength>100</DescriptionLength><Hierarchical>false</Hierarchical></Properties></Catalog></MetaDataObject>");
            var metadata = MetadataTypes.Enrich(new MetadataIndex("test", "Тест", root, DateTime.Now, [
                new("Документы.Заказ", "Заказ", "", [], []), new("Справочники.Партнеры", "Партнеры", "", [], [])]) { SourceKind = "xml-folder" }, default);
            CompletionChoice[] Find(string text, MetadataIndex[]? sources = null)
            {
                var prefix = new string(text.Reverse().TakeWhile(c => char.IsLetterOrDigit(c) || c == '_').Reverse().ToArray());
                return TypedCompletion.Find(new(IntPtr.Zero, text, text.Length, prefix, 0, 0), sources ?? [metadata]);
            }
            string[] Names(string text) => Find(text).Select(c => c.InsertText!).ToArray();
            const string doc = "Док = Документы.Заказ.СоздатьДокумент();\n";
            Check(new[] { "Номер", "Дата", "Проведен", "Партнер", "Товары" }.All(Names(doc + "Док.").Contains), "Созданный документ: стандартные и собственные реквизиты");
            Check(Names(doc + "Док.Партнер.").Contains("Наименование"), "Цепочка через ссылочный реквизит");
            Check(Names(doc + "Док.Неоднозначный.").Length == 0, "Составной тип не подменяется единственной ссылкой");
            Check(Names(doc + "Строка = Док.Товары.Добавить();\nСтрока.").Contains("Цена"), "Строка табличной части через Добавить");
            Check(Names(doc + "Док.Товары[0].").Contains("Цена") && Names(doc + "Док.Товары.Добавить().").Contains("Цена"), "Индекс и цепочка вызовов табличной части");
            Check(!Names("П = Справочники.Партнеры.СоздатьЭлемент();\nП.").Contains("Код"), "Отключённый стандартный реквизит не предлагается");
            const string table = "Т = Новый ТаблицаЗначений;\nТ.Колонки.Добавить(\"Цена\", Новый ОписаниеТипов(\"Число\"));\nТ.Колонки.Добавить(\"Товар\");\n";
            Check(Find(table + "Р = Т.Добавить();\nР.").Single(c => c.InsertText == "Цена").Label.Contains("Число"), "Колонки строки таблицы с типами");
            Check(Names(table + "Копия = Т;\nСтр = Копия[0];\nСтр.").Contains("Товар"), "Алиасы таблиц и индексирование");
            Check(Names(table + "Для Каждого Стр Из Т Цикл\nСтр.").Contains("Цена"), "Строка таблицы в цикле");
            Check(Names(table + "Для Каждого Стр Из Т Цикл\nКонецЦикла;\nСтр.").Length == 0, "Переменная цикла не выходит за область видимости");
            Check(!Names(table + "Т.Колонки.Удалить(\"Цена\");\nСтр = Т.Добавить();\nСтр.").Contains("Цена"), "Удалённая колонка не предлагается");
            Check(Names(table + "Т.Колонки.Очистить();\nСтр = Т.Добавить();\nСтр.").Length == 0, "Очистка колонок учитывается");
            Check(Names(table + "К = Т.СкопироватьКолонки(\"Цена\");\nК.Добавить().").SequenceEqual(["Цена"]), "Выборочное копирование колонок");
            Check(Names(table + "Т.Колонки.Удалить(Индекс);\nТ.Добавить().").Length == 0, "Неизвестная удалённая колонка не угадывается");
            Check(Names("С = Новый Структура(\"Вложенная\", Новый Структура(\"Сумма\", 0));\nКопия = С;\nКопия.Вложенная.").SequenceEqual(["Сумма"]), "Вложенные структуры и алиасы");
            Check(Names("С = Новый Структура(\"Первое\");\nС = Новый Структура(\"Второе\");\nС.").SequenceEqual(["Второе"]), "Повторное присваивание заменяет состав");
            Check(Names("Если Условие Тогда\nС = Новый Структура(\"Первое\");\nИначе\nС = Новый Структура(\"Второе\");\nКонецЕсли;\nС.").Length == 0, "Разные поля ветвей не смешиваются");
            Check(Names("Если Условие Тогда\nС = Новый Структура(\"Общее,Первое\");\nИначе\nС = Новый Структура(\"Общее,Второе\");\nКонецЕсли;\nС.").SequenceEqual(["Общее"]), "Общее поле ветвей сохраняется");
            Check(Names("С = Новый Структура(\"Данные\");\nС.Данные = Новый Структура(\"Цена\");\nС.Данные.").SequenceEqual(["Цена"]), "Присваивание вложенному полю");
            Check(Names("Функция Сделать()\nТ = Новый ТаблицаЗначений;\nТ.Колонки.Добавить(\"Сумма\");\nВозврат Т;\nКонецФункции\nПроцедура Тест()\nТаб = Сделать();\nР = Таб.Добавить();\nР.").Contains("Сумма"), "Таблица возвращена локальной функцией");
            Check(Names("Функция Обернуть(Знач Вход)\nВозврат Новый Структура(\"Данные\", Вход);\nКонецФункции\nПроцедура Тест()\nС = Обернуть(Новый Структура(\"Цена\"));\nС.Данные.").Contains("Цена"), "Тип аргумента передан в возвращаемую структуру");
            var common = Path.Combine(root, "CommonModules", "Фабрика", "Ext"); Directory.CreateDirectory(common);
            File.WriteAllText(Path.Combine(common, "Module.bsl"), "Функция Создать() Экспорт\nВозврат Новый Структура(\"Покупатель,Сумма\");\nКонецФункции");
            Check(Names("Д = Фабрика.Создать();\nД.").Contains("Покупатель"), "Структура возвращена общим модулем");
            Check(Names("Процедура Тест(Фабрика)\nД = Фабрика.Создать();\nД.").Length == 0, "Параметр скрывает имя общего модуля");
            Check(Names("Процедура Тест()\nС = Новый Структура(\"НеПоказывать\");\nКонецПроцедуры\nС.").Length == 0, "Локальная переменная не выходит в код модуля");
            var extension = Path.Combine(root, "extension");
            var extensionModule = Path.Combine(extension, "CommonModules", "Фабрика", "Ext"); Directory.CreateDirectory(extensionModule);
            File.WriteAllText(Path.Combine(extensionModule, "Module.bsl"), "Функция Другая() Экспорт\nВозврат Новый Структура(\"Другое\");\nКонецФункции");
            var extensionIndex = metadata with { Source = extension, ExtensionName = "ТестРасширение" };
            Check(Find("Д = Фабрика.Создать();\nД.", [metadata, extensionIndex]).Any(c => c.InsertText == "Покупатель"), "Расширение без метода не скрывает основной метод");
            Check(Names("// Возвращаемое значение:\n// Структура - результат\n// * Сумма - Число - итог\nФункция ПоОписанию()\nВозврат НеизвестныйВызов();\nКонецФункции\nПроцедура Тест()\nД = ПоОписанию();\nД.").Contains("Сумма"), "Контракт возвращаемого значения из комментария");
            Check(Names("Функция Рекурсия()\nВозврат Рекурсия();\nКонецФункции\nПроцедура Тест()\nД = Рекурсия();\nД.").Length == 0, "Рекурсия ограничена");
            Check(Names("С = " + new string('(', 200) + "1" + new string(')', 200) + ";\nС.").Length == 0, "Глубина выражения ограничена");
            var integrated = doc + "Док.Да";
            Check(SmartCompletion.Find(new(IntPtr.Zero, integrated, integrated.Length, "Да", 0, 0), [], metadata,
                new CompletionHistory(Path.Combine(root, "history.json")), false).Any(c => c.InsertText == "Дата"), "Интеграция типов и фильтра по префиксу");
            if (livePath is not null)
            {
                var store = new MetadataStore(livePath);
                var live = store.Caches.Where(c => c.SourceKind == "xml-folder").Select(c => MetadataTypes.Enrich(c, default)).ToArray();
                var fields = Find("Док = Документы.Событие.СоздатьДокумент();\nДок.", live).Select(c => c.InsertText).ToArray();
                Check(fields.Contains("Номер") && fields.Contains("Дата"), "Реальная выгрузка: стандартные поля документа Событие");
                Check(live.Any(c => c.Entries.Any(e => e.Types.Length > 0)), "Реальная выгрузка: типы XML прочитаны");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "typed-test-result.txt"), "PASS\n" + string.Join('\n', passed)); return 0;
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (Path.GetDirectoryName(path) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(path).StartsWith("qadam-types-")) throw new IOException("Некорректная папка теста");
            Directory.Delete(path, true);
        }
    }
}
