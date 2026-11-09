using System.IO;

namespace Qadam1C;

internal static class ConfigurationFolderChecks
{
    internal static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "qadam-folder-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, "ConfigDumpInfo.xml");
        var config = Path.Combine(folder, "Configuration.xml");
        const string xml = "<ConfigDumpInfo xmlns='http://v8.1c.ru/8.3/xcf/dumpinfo' format='Hierarchical'><ConfigVersions><Metadata name='Configuration.Демо' id='abc'/><Metadata name='Catalog.Товары' id='def'><Metadata name='Catalog.Товары.Attribute.Артикул' id='ghi'/></Metadata></ConfigVersions></ConfigDumpInfo>";
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Rejected(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; } catch (System.Xml.XmlException) { return; }
            throw new Exception("Некорректная выгрузка принята.");
        }
        try
        {
            Rejected(() => ConfigurationFolder.Read(folder, null, default));
            File.WriteAllText(config, "<MetaDataObject><Configuration uuid='abc'><Properties><Name>Демо</Name></Properties></Configuration></MetaDataObject>");
            File.WriteAllText(manifest, xml);
            File.WriteAllText(Path.Combine(folder, "Module.bsl"), "Процедура Тест()\nКонецПроцедуры");
            var i = ConfigurationFolder.Read(folder, null, default);
            Check(i.Name == "Демо" && i.ModuleCount == 1 && i.ConfigurationId == "abc", "Не прочитана конфигурация.");
            Check(i.Entries.Any(e => e.Path == "Справочники.Товары.Реквизиты.Артикул"), "Не прочитан вложенный объект.");
            var state = Path.Combine(folder, "state.json");
            var store = new MetadataStore(state);
            store.SaveConfiguration(i);
            Check(store.For("base:other") is null, "Импорт сам подключил базу.");
            store.Bind("base:one", i.Id);
            var renamed = i with { Name = "Тестовая база" };
            store.SaveConfiguration(renamed);
            var refreshed = ConfigurationFolder.Read(folder, renamed, default);
            Check(refreshed.Id == i.Id && refreshed.Name == renamed.Name, "Обновление потеряло имя или идентичность.");
            store.SaveConfiguration(refreshed);
            Check(new MetadataStore(state).For("base:one")?.Name == renamed.Name, "Подключение потеряно при обновлении.");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { ConfigurationFolder.Read(folder, i, cts.Token); throw new Exception("Отмена не сработала."); }
            catch (OperationCanceledException) { }
            File.WriteAllText(manifest, xml.Replace("id='abc'", "id='wrong'"));
            Rejected(() => ConfigurationFolder.Read(folder, i, default));
            Check(new MetadataStore(state).For("base:one")?.ConfigurationId == "abc", "Ошибка испортила сохранённые данные.");
            File.WriteAllText(manifest, "<!DOCTYPE x [<!ENTITY test SYSTEM 'file:///missing'>]>" + xml);
            Rejected(() => ConfigurationFolder.Read(folder, null, default));
            File.WriteAllText(manifest, xml);
            File.WriteAllText(config, "<MetaDataObject><Configuration uuid='abc'><Properties><Name>Демо</Name><ConfigurationExtensionPurpose>Customization</ConfigurationExtensionPurpose></Properties></Configuration></MetaDataObject>");
            Rejected(() => ConfigurationFolder.Read(folder, null, default));
            store.Remove(i.Id);
            Check(store.Caches.Count == 0 && store.For("base:one") is null && File.Exists(config), "Удаление затронуло источник или оставило связь.");
            // Read old persisted data without new fields.
            File.WriteAllText(state, "{\"Caches\":[{\"Id\":\"old\",\"Name\":\"Старая\",\"Source\":\"test.mxl\",\"Updated\":\"2026-01-01T00:00:00\",\"Entries\":[]}],\"Bindings\":{\"base:old\":\"old\"}}");
            store = new MetadataStore(state);
            Check(store.For("base:old")?.Name == "Старая", "Нарушена совместимость MXL.");
            using var form = new MetadataWindow(store);
            Check(form.Text == "Qadam1C — Мои конфигурации", "Повреждена кодировка заголовка.");
            form.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "configuration-window.png"));
            form.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "configuration-test-result.txt"),
                "PASS: manual import, nested objects, modules, no implicit binding, rename, refresh, persistence, cancellation, inconsistent export, DTD, extension rejection, remove without source deletion, legacy MXL, window rendering.");
            return 0;
        }
        finally
        {
            var resolved = Path.GetFullPath(folder);
            if (Path.GetDirectoryName(resolved) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(resolved).StartsWith("qadam-folder-check-", StringComparison.Ordinal))
                throw new IOException("Недопустимый путь очистки теста.");
            Directory.Delete(resolved, true);
        }
    }
}
