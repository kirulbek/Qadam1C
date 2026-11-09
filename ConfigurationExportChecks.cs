using System.IO;
using System.Text.Json;

namespace Qadam1C;

internal static class ConfigurationExportChecks
{
    internal static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qadam-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = new List<string>();
        void Check(bool test, string name) { if (!test) throw new Exception(name); passed.Add(name); }
        try
        {
            var rows = InfobaseCatalog.Sections("[Группа]\nFolder=/\n[Демо]\nConnect=Srvr=\"localhost\";Ref=\"demo\";Usr=\"private\";Pwd=\"secret\";\nDefaultVersion=8.3.26\n[Веб]\nConnect=ws=\"https://example.invalid\";\n").ToArray();
            Check(InfobaseCatalog.Parse(rows[0]) is null && InfobaseCatalog.Parse(rows[2]) is null, "Не запускаем группы и веб-базы");
            var b = InfobaseCatalog.Parse(rows[1])!;
            Check(InfobaseCatalog.VersionMatches("8.3.26.1540", "8.3.26.*") &&
                InfobaseCatalog.VersionMatches("8.3.26.1540", "8.3") &&
                !InfobaseCatalog.VersionMatches("8.3.26.1540", "8.3.2") &&
                !InfobaseCatalog.VersionMatches("8.3.26.1540", "8.3.26.154"), "Версия платформы сравнивается по компонентам");
            Check(b.Name == "Демо" && b.Address == "localhost\\demo" && !JsonSerializer.Serialize(b).Contains("secret"), "Пароли не входят в модель базы");
            var command = DesignerExport.Command(b, "C:\\1cv8\\bin\\1cv8.exe", "C:\\Каталог с пробелами", "C:\\Log.txt", true);
            Check(command.ArgumentList.Contains("-AllExtensions") && !command.UseShellExecute &&
                command.ArgumentList.Count(a => a == "/DumpConfigToFiles") == 1 && !string.Join(' ', command.ArgumentList).Contains("secret"), "Штатная команда и разделение аргументов");
            Check(ExportPaths.FolderName(b) != ExportPaths.FolderName(b with { Key = "base:FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" }), "Одноимённые базы разделены");
            try { ExportPaths.Child(directory, "..\\outside"); throw new Exception("Выход из папки разрешён"); } catch (IOException) { passed.Add("Выход из папки запрещён"); }
            var store = new MetadataStore(Path.Combine(directory, "state.json"));
            var exports = Path.Combine(directory, "exports");
            Task Fake(Infobase _, string exe, string folder, string log, bool ext, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Write(ext ? Path.Combine(folder, "АБ") : folder, ext);
                return Task.CompletedTask;
            }
            var progress = new Progress<string>();
            ConfigurationExport.Export(b, "fake", exports, true, store, progress, default, Fake).GetAwaiter().GetResult();
            var first = ConfigurationExport.Destination(exports, b);
            var main = store.For(b.Key)!;
            Check(File.Exists(Path.Combine(first, "Основная", "Configuration.xml")) && File.Exists(Path.Combine(first, "Расширения", "АБ", "Configuration.xml")), "Раздельные каталоги основной и расширения");
            Check(store.ForDocument(b.Key, "Модуль [Расширение АБ]")?.ExtensionName == "АБ" && store.ForDocument(b.Key, "[Расширение Другое]") is null, "Расширения не подменяются основной конфигурацией");
            var modules = JsonSerializer.Deserialize<IndexedModule[]>(File.ReadAllText(Path.Combine(first, "Основная", ".qadam-modules.json")))!;
            Check(modules.Single().Methods.Single().Name == "Проверка" && modules[0].Sha256.Length == 64, "Индекс BSL содержит методы и хеши");
            ConfigurationExport.Export(b with { Name = "Демо переименовано" }, "fake", exports, true, store, progress, default, Fake).GetAwaiter().GetResult();
            Check(ConfigurationExport.Destination(exports, b) == first && store.For(b.Key)?.Id == main.Id, "Переименование сохраняет каталог и индекс");
            Task Fail(Infobase baseInfo, string exe, string folder, string log, bool ext, CancellationToken token) => throw new IOException("Сбой процесса 1С");
            try { ConfigurationExport.Export(b, "fake", exports, true, store, progress, default, Fail).GetAwaiter().GetResult(); throw new Exception("Ошибка не передана"); }
            catch (IOException) { }
            Check(store.For(b.Key)?.Id == main.Id && File.Exists(Path.Combine(first, "Основная", "Configuration.xml")), "Ошибка сохраняет прежнюю выгрузку");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { ConfigurationExport.Export(b, "fake", exports, true, store, progress, cts.Token, Fake).GetAwaiter().GetResult(); throw new Exception("Отмена не передана"); }
            catch (OperationCanceledException) { passed.Add("Отмена не публикует результат"); }
            var failedPath = Path.Combine(directory, "blocked.json");
            var blockedStore = new MetadataStore(failedPath); Directory.CreateDirectory(failedPath);
            try { ConfigurationExport.Export(b, "fake", exports, true, blockedStore, progress, default, Fake).GetAwaiter().GetResult(); throw new Exception("Ошибка сохранения не передана"); }
            catch (IOException) { passed.Add("Ошибка сохранения индекса вызывает откат каталогов"); }
            catch (UnauthorizedAccessException) { passed.Add("Ошибка сохранения индекса вызывает откат каталогов"); }
            Check(File.Exists(Path.Combine(first, "Основная", ".qadam-modules.json")), "После отката прежние файлы на месте");
            var live = InfobaseCatalog.Load(out var warnings);
            Check(live.Length > 0 && live.All(item => item.Key.StartsWith("base:")), "Список баз этого компьютера прочитан без запуска 1С");
            using (var form = new ExportWizard(store, [b]))
            {
                form.Show(); Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "export-wizard.png"));
                form.Close();
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "export-test-result.txt"), "PASS\n" + string.Join('\n', passed));
            return 0;
        }
        finally
        {
            var path = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(path) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(path).StartsWith("qadam-export-test-"))
                throw new IOException("Некорректный каталог очистки.");
            Directory.Delete(path, true);
        }
    }
    private static void Write(string path, bool extension)
    {
        Directory.CreateDirectory(path);
        var id = extension ? "ext-id" : "main-id"; var name = extension ? "АБ" : "Демо";
        File.WriteAllText(Path.Combine(path, "Configuration.xml"), $"<MetaDataObject><Configuration uuid='{id}'><Properties><Name>{name}</Name>" +
            (extension ? "<ConfigurationExtensionPurpose>Customization</ConfigurationExtensionPurpose>" : "") + "</Properties></Configuration></MetaDataObject>");
        File.WriteAllText(Path.Combine(path, "ConfigDumpInfo.xml"), $"<ConfigDumpInfo xmlns='http://v8.1c.ru/8.3/xcf/dumpinfo' format='Hierarchical'><ConfigVersions><Metadata name='Configuration.{name}' id='{id}'/></ConfigVersions></ConfigDumpInfo>");
        File.WriteAllText(Path.Combine(path, "Module.bsl"), "Процедура Проверка(Параметр) Экспорт\nКонецПроцедуры");
    }
}
