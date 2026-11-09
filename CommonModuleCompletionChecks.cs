using System.IO;
using System.Text.Json;

namespace Qadam1C;

internal static class CommonModuleCompletionChecks
{
    internal static int Run(string? liveState = null, int? livePid = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "qadam-methods-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed.Add(name); }
        try
        {
            Check(BaseIdentity.RegisteredName("1cv8.exe DESIGNER /IBName\"УНФ_АБ\"") == "УНФ_АБ" &&
                BaseIdentity.RegisteredName("1cv8.exe DESIGNER /IBName \"База с пробелами\"") == "База с пробелами" &&
                BaseIdentity.RegisteredName("1cv8.exe /IBNameExtra") is null, "Имя базы распознаётся с пробелом и без него");
            var history = new CompletionHistory(Path.Combine(root, "history.json"));
            var main = new MetadataIndex("main", "Основная", Path.Combine(root, "main"), DateTime.Now, [])
                { BaseKey = "base:test", SourceKind = "xml-folder" };
            var ext = main with { Id = "ext", Source = Path.Combine(root, "ext"), ExtensionName = "РасширениеАБ" };
            void Write(MetadataIndex index, string text)
            {
                Directory.CreateDirectory(index.Source);
                var moduleFile = Path.Combine(index.Source, "CommonModules", "АБ_ОбщегоНазначения", "Ext", "Module.bsl");
                Directory.CreateDirectory(Path.GetDirectoryName(moduleFile)!);
                File.WriteAllText(moduleFile, text);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(moduleFile)));
                File.WriteAllText(Path.Combine(index.Source, ".qadam-modules.json"), JsonSerializer.Serialize(new[] {
                    new IndexedModule("CommonModules/АБ_ОбщегоНазначения/Ext/Module.bsl", hash, ModuleOutline.Parse(text)),
                    new IndexedModule("Documents/Test/Ext/ObjectModule.bsl", "hash", ModuleOutline.Parse("Процедура Чужой() Экспорт\nКонецПроцедуры")) }));
            }
            Write(main, "Функция Рассчитать(Цена, Количество) Экспорт\nКонецФункции\nПроцедура Скрытый()\nКонецПроцедуры\nПроцедура БезПараметров() Экспорт\nКонецПроцедуры");
            Write(ext, "Функция Рассчитать(Цена) Экспорт\nКонецФункции\nПроцедура Дополнительный() Экспорт\nКонецПроцедуры");
            CompletionChoice[] Find(string text, IReadOnlyList<MetadataIndex> sources, string suffix = "")
            {
                var prefix = new string(text.Reverse().TakeWhile(c => char.IsLetterOrDigit(c) || c == '_').Reverse().ToArray());
                return SmartCompletion.Find(new(IntPtr.Zero, text + suffix, text.Length, prefix, 0, 0, "base:test"), [],
                    sources.LastOrDefault(), history, false, sources);
            }
            const string dotted = "АБ_ОбщегоНазначения.";
            var mainChoices = Find(dotted, [main]);
            Check(mainChoices.Length == 2 && mainChoices.All(c => c.SearchText is not "Скрытый" and not "Чужой"), "Только экспортные методы выбранного общего модуля");
            var method = mainChoices.Single(c => c.SearchText == "Рассчитать");
            var definition = method.Definition ?? throw new Exception("Нет цели определения");
            var document = DefinitionReader.Read(definition);
            Check(document.Method.Name == "Рассчитать" && document.Method.Line == 1, "Определение открывается из файла выбранного метода");
            using (var form = new MethodDefinitionWindow(document))
            {
                form.Show(); Application.DoEvents();
                Check(form.Code.ReadOnly && form.Code.SelectionStart == form.DefinitionOffset && form.Code.SelectedText.Contains("Рассчитать"), "Просмотр только для чтения и выделение определения");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "definition-preview.png"));
                form.Close();
            }
            try { DefinitionReader.Read(definition with { File = "../../outside.bsl" }); throw new Exception("Разрешён выход из каталога"); }
            catch (InvalidDataException) { passed.Add("Переход вне папки выгрузки запрещён"); }
            var sourceFile = Path.Combine(definition.Folder, definition.File);
            var original = File.ReadAllText(sourceFile);
            File.AppendAllText(sourceFile, "\n// изменено после индексации");
            try { DefinitionReader.Read(definition); throw new Exception("Не замечен изменённый файл"); }
            catch (InvalidDataException) { passed.Add("Изменение файла после индексации останавливает переход"); }
            File.WriteAllText(sourceFile, original);
            Check(method.Label.Contains("Цена, Количество") && method.InsertText == "Рассчитать()" && method.Cursor == "Рассчитать(".Length, "Параметры, скобки и курсор внутри вызова");
            Check(Find(dotted + "Расс", [main], "(1)").Single().InsertText == "Рассчитать", "Готовые скобки не дублируются");
            Check(Find(dotted + "Расс", [main]).Single().ReplaceLength == 4, "Фильтрация и замена только префикса метода");
            Check(Find("// " + dotted, [main]).Length == 0 && Find("Текст = \"" + dotted, [main]).Length == 0, "Нет подсказок в комментариях и строках");
            Check(Find("Другой." + dotted, [main]).Length == 0, "Нет подмены длинной цепочки общим модулем");
            Check(Find("АБ_ОбщегоНазначения = Новый Структура;\n" + dotted, [main]).Length == 0, "Локальная переменная не считается общим модулем");
            Check(Find("Процедура Тест(АБ_ОбщегоНазначения)\n" + dotted, [main]).Length == 0, "Параметр не считается общим модулем");
            Check(Find(dotted, []).Length == 0, "Методы не переносятся в неподключённую базу");
            var merged = Find(dotted, [main, ext]);
            Check(merged.Single(c => c.SearchText == "Рассчитать").Definition?.Folder == ext.Source &&
                merged.Single(c => c.SearchText == "БезПараметров").Definition?.Folder == main.Source, "Определения основной и расширения ведут к своим файлам");
            Check(merged.Length == 3 && merged.Single(c => c.SearchText == "Рассчитать").Label.Contains("Рассчитать(Цена)"), "Основная и выбранное расширение объединены без дубликатов");
            File.WriteAllText(Path.Combine(ext.Source, "Configuration.xml"), "<MetaDataObject><Configuration><Properties><Synonym><item><lang>ru</lang><content>АБ</content></item></Synonym></Properties></Configuration></MetaDataObject>");
            var store = new MetadataStore(Path.Combine(root, "metadata.json")); store.PublishBase("base:test", [main, ext]);
            Check(store.CommonModuleSources("base:test", "Модуль [Расширение АБ]").Length == 2 &&
                store.CommonModuleSources("base:test", "Модуль [Расширение Неизвестное]").Length == 0, "Синоним расширения распознаётся, неизвестное не угадывается");
            Write(main, "Процедура НовыйМетодПослеОбновления() Экспорт\nКонецПроцедуры");
            Check(Find(dotted, [main]).Single().SearchText == "НовыйМетодПослеОбновления", "Кэш обновляется после замены индекса");
            File.WriteAllText(Path.Combine(main.Source, ".qadam-modules.json"), "broken");
            Check(Find(dotted, [main]).Length == 0, "Повреждённый индекс не ломает подсказки");
            if (liveState is not null)
            {
                var live = new MetadataStore(liveState);
                var owner = live.Caches.Single(c => c.ExtensionName is null && c.SourceKind == "xml-folder");
                var index = JsonSerializer.Deserialize<IndexedModule[]>(File.ReadAllText(Path.Combine(owner.Source, ".qadam-modules.json")))!;
                var expected = index.Single(m => m.File.Replace('\\', '/') == "CommonModules/АБ_ОбщегоНазначения/Ext/Module.bsl").Methods.Where(m => m.Exported).ToArray();
                var actual = Find(dotted, [owner]);
                var liveDocument = DefinitionReader.Read(actual.Single(c => c.SearchText == "ПолучитьТемыСобытия").Definition!);
                Check(liveDocument.Method.Name == "ПолучитьТемыСобытия" && liveDocument.Method.Line > 0, "Реальная выгрузка: определение ПолучитьТемыСобытия найдено и хеш совпадает");
                Check(expected.Length == 11 && expected.All(m => actual.Any(c => c.SearchText == m.Name)), "Реальная выгрузка: все 11 экспортных методов доступны после точки");
                Check(live.CommonModuleSources(owner.BaseKey!, "Модуль [Расширение АБ]").Length == 2, "Реальная выгрузка: короткое имя расширения АБ распознано");
                if (livePid is { } pid)
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    var key = BaseIdentity.Get(pid, process.StartTime.ToUniversalTime().Ticks);
                    Check(live.For(key)?.Id == owner.Id, "Открытый Конфигуратор сопоставлен с выгруженной базой");
                    var sources = live.CommonModuleSources(key, process.MainWindowTitle);
                    Check(expected.All(m => Find(dotted, sources).Any(c => c.SearchText == m.Name)), "Контекст открытого Конфигуратора получает экспортные методы основной конфигурации");
                }
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "module-completion-test-result.txt"), "PASS\n" + string.Join('\n', passed));
            return 0;
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(resolved).StartsWith("qadam-methods-test-")) throw new IOException("Некорректная папка теста.");
            Directory.Delete(resolved, true);
        }
    }
}
