using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Qadam1C;

internal static class ExportPaths
{
    internal static string Child(string root, string relative)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(boundary, relative));
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь выходит за общую папку.");
        for (var node = new DirectoryInfo(path); node is not null; node = node.Parent)
            if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Для выгрузки выберите обычную локальную папку без ссылок и перенаправлений.");
        return path;
    }
    internal static string FolderName(Infobase b)
    {
        var name = new string(b.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 60) name = name[..60];
        if (name.Length == 0) name = "База";
        return name + " — " + b.Key[5..17];
    }
}

internal static class DesignerExport
{
    internal static ProcessStartInfo Command(Infobase b, string executable, string directory, string log, bool extensions)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        // No shell, credentials, startup snippets, import, database update or user-supplied switches.
        foreach (var value in new[] { "DESIGNER", b.Option, b.Address, "/DumpConfigToFiles", directory, "-Format", "Hierarchical" })
            start.ArgumentList.Add(value);
        if (extensions) start.ArgumentList.Add("-AllExtensions");
        start.ArgumentList.Add("/Out"); start.ArgumentList.Add(log);
        return start;
    }
    internal static void CheckClosed(Infobase b)
    {
        foreach (var window in ConfiguratorTarget.List())
        {
            if (window.Key == b.Key)
                throw new InvalidOperationException($"Сохраните изменения и закройте Конфигуратор базы «{b.Name}», затем повторите выгрузку. Qadam не закрывает его самостоятельно.");
            if (!window.Key.StartsWith("base:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Не удалось определить базу открытого Конфигуратора (PID {window.Pid}). Закройте это окно после сохранения изменений и повторите проверку.");
        }
    }
    internal static async Task Run(Infobase b, string exe, string directory, string log, bool extensions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CheckClosed(b);
        Directory.CreateDirectory(directory);
        using var process = Process.Start(Command(b, exe, directory, log, extensions)) ?? throw new IOException("Не удалось запустить платформу 1С.");
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            // Only this batch process is owned by Qadam. Never terminate other Designer windows.
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException($"1С не завершила выгрузку «{b.Name}» (код {process.ExitCode}). Возможны блокировка базы или отмена входа. Прежние данные сохранены. Журнал: {log}");
    }
}

internal sealed record ExportMarker(string BaseKey, string Name, DateTime Updated);
internal sealed record IndexedModule(string File, string Sha256, ModuleMethod[] Methods);

internal static class ConfigurationExport
{
    internal delegate Task ExportStep(Infobase b, string exe, string directory, string log, bool extensions, CancellationToken token);
    internal static string Destination(string root, Infobase b)
    {
        if (Directory.Exists(root))
        {
            var matching = Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => File.Exists(Path.Combine(d, ".qadam-base.json")))
                .Where(d => JsonSerializer.Deserialize<ExportMarker>(File.ReadAllText(ExportPaths.Child(root,
                    Path.GetRelativePath(root, Path.Combine(d, ".qadam-base.json")))) )?.BaseKey == b.Key).ToArray();
            if (matching.Length > 1) throw new IOException("В общей папке найдены две выгрузки одной базы. Выберите другую общую папку или устраните дубликат.");
            if (matching.Length == 1) return ExportPaths.Child(root, Path.GetRelativePath(root, matching[0]));
        }
        return ExportPaths.Child(root, ExportPaths.FolderName(b));
    }
    internal static async Task Export(Infobase b, string exe, string root, bool extensions, MetadataStore store,
        IProgress<string> progress, CancellationToken token, ExportStep? run = null)
    {
        root = Path.GetFullPath(root);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root);
        using var exclusive = new FileStream(ExportPaths.Child(root, ".qadam-export.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var destination = Destination(root, b);
        if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, ".qadam-base.json")))
            throw new IOException("Папка назначения уже существует и не принадлежит Qadam. Выберите другую общую папку.");
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var staging = ExportPaths.Child(root, Path.Combine(".подготовка", runId));
        Directory.CreateDirectory(staging);
        var main = ExportPaths.Child(staging, "Основная");
        var extRoot = ExportPaths.Child(staging, "Расширения");
        run ??= DesignerExport.Run;
        progress.Report($"{b.Name}: выгрузка основной конфигурации. Если появится окно входа 1С, выполните вход.");
        await run(b, exe, main, Path.Combine(staging, "основная.log"), false, token);
        if (extensions)
        {
            progress.Report($"{b.Name}: выгрузка расширений…");
            await run(b, exe, extRoot, Path.Combine(staging, "расширения.log"), true, token);
        }
        var folders = new List<(string Path, bool Extension)> { (main, false) };
        if (extensions && Directory.Exists(extRoot))
            folders.AddRange(Directory.EnumerateDirectories(extRoot).Select(p => (ExportPaths.Child(staging, Path.GetRelativePath(staging, p)), true)));
        var indexes = new List<MetadataIndex>();
        foreach (var (folder, extension) in folders)
        {
            progress.Report($"{b.Name}: индексирование {Path.GetFileName(folder)}…");
            var index = await Task.Run(() => ConfigurationFolder.Read(folder, null, token, allowExtension: extension), token);
            if (extension != (index.ExtensionName is not null)) throw new InvalidDataException("Основная конфигурация и расширение перепутаны в выгрузке.");
            var modules = await Task.Run(() => IndexModules(folder, token), token);
            File.WriteAllText(Path.Combine(folder, ".qadam-modules.json"), JsonSerializer.Serialize(modules));
            var old = store.Caches.FirstOrDefault(c => c.BaseKey == b.Key && c.ConfigurationId == index.ConfigurationId && c.ExtensionName == index.ExtensionName);
            indexes.Add(index with { Id = old?.Id ?? index.Id, BaseKey = b.Key,
                Name = b.Name + (extension ? " / " + index.ExtensionName : " / Основная"),
                Source = ExportPaths.Child(destination, Path.GetRelativePath(staging, folder)) });
        }
        if (indexes.Select(i => i.ConfigurationId).Distinct().Count() != indexes.Count)
            throw new InvalidDataException("Выгрузка содержит повторяющиеся конфигурации.");
        File.WriteAllText(Path.Combine(staging, ".qadam-base.json"), JsonSerializer.Serialize(new ExportMarker(b.Key, b.Name, DateTime.Now)));
        token.ThrowIfCancellationRequested();
        var history = ExportPaths.Child(root, Path.Combine(".предыдущие", Path.GetFileName(destination) + "-" + runId));
        var hadPrevious = Directory.Exists(destination);
        // All moves are between verified children of the user-selected root. No source directory is deleted.
        if (hadPrevious) { Directory.CreateDirectory(Path.GetDirectoryName(history)!); Directory.Move(destination, history); }
        var published = false;
        try
        {
            Directory.Move(staging, destination); published = true;
            store.PublishBase(b.Key, indexes);
        }
        catch
        {
            if (published) Directory.Move(destination, staging);
            if (hadPrevious) Directory.Move(history, destination);
            throw;
        }
        progress.Report($"{b.Name}: готово. Конфигураций: {indexes.Count}, модулей: {indexes.Sum(i => i.ModuleCount)}. Можно открыть Конфигуратор.");
    }
    internal static IndexedModule[] IndexModules(string folder, CancellationToken token)
    {
        var modules = new List<IndexedModule>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.bsl", new EnumerationOptions {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            var path = ExportPaths.Child(folder, Path.GetRelativePath(folder, file));
            var bytes = File.ReadAllBytes(path);
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            modules.Add(new(Path.GetRelativePath(folder, file), Convert.ToHexString(SHA256.HashData(bytes)), ModuleOutline.Parse(text)));
        }
        return modules.ToArray();
    }
}
