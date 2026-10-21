using System.IO;
using Microsoft.Win32;

namespace Qadam1C;

internal static class AppDataPaths
{
    // Portable builds retain their existing data. Installer builds keep user data across upgrades.
    internal static string FilePath(string name) => Path.Combine(
        File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.txt"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qadam1C", "data")
            : Path.Combine(AppContext.BaseDirectory, "data"), name);
}

internal static class WindowsStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return string.Equals(key?.GetValue("Qadam1C") as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value) key.SetValue("Qadam1C", Command);
            else key.DeleteValue("Qadam1C", throwOnMissingValue: false);
        }
    }
    private static string Command => "\"" + Environment.ProcessPath + "\"";
}
