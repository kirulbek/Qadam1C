using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal static class BaseIdentity
{
    private static readonly ConcurrentDictionary<string, string> Keys = new();

    public static string Get(int pid, long started)
    {
        var session = $"{pid}:{started}";
        return Keys.GetOrAdd(session, _ =>
        {
            object? locator = null, service = null, rows = null;
            try
            {
                locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
                service = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
                rows = ((dynamic)service).ExecQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
                foreach (var row in (System.Collections.IEnumerable)rows)
                {
                    try { return FromCommandLine((string?)((dynamic)row).CommandLine) ?? session; }
                    finally { Release(row); }
                }
            }
            catch { /* Access may be restricted: never guess a database from its display title. */ }
            finally { Release(rows); Release(service); Release(locator); }
            return session;
        });
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    internal static string? FromCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var argv = CommandLineToArgvW(command, out var count);
        if (argv == IntPtr.Zero) return null;
        var args = new string[count];
        try
        {
            for (var i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
        }
        finally { LocalFree(argv); }
        string? file = null, server = null, connection = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            foreach (var option in new[] { "/IBConnectionString", "/F", "/S" })
            {
                // /S must not accidentally match /StartupMessages or other switches.
                if (!a.Equals(option, StringComparison.OrdinalIgnoreCase) &&
                    !(option is "/F" or "/S" && a.StartsWith(option, StringComparison.OrdinalIgnoreCase) &&
                      a.Length > 2 && (option == "/F" ? System.IO.Path.IsPathFullyQualified(a[2..]) : a[2..].Contains('\\')))) continue;
                var value = a.Length > option.Length ? a[option.Length..] : i + 1 < args.Length ? args[++i] : "";
                if (option == "/F") file = value;
                else if (option == "/S") server = value;
                else connection = value;
                break;
            }
        }
        if (connection is not null)
        {
            string? Property(string name)
            {
                var match = Regex.Match(connection, @"(?:^|;)\s*" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^;]*))", RegexOptions.IgnoreCase);
                return match.Success ? match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.Trim() : null;
            }
            file = Property("File");
            var host = Property("Srvr"); var database = Property("Ref");
            server = host is not null && database is not null ? host + "\\" + database : null;
        }
        string? identity = null;
        if (!string.IsNullOrWhiteSpace(file) && System.IO.Path.IsPathFullyQualified(file))
            identity = "file:" + System.IO.Path.GetFullPath(file).TrimEnd('\\', '/').ToUpperInvariant();
        else if (!string.IsNullOrWhiteSpace(server) && server.Contains('\\'))
            identity = "server:" + server.Trim().ToUpperInvariant();
        // Persist only a fingerprint; credentials and the command line never enter the cache.
        return identity is null ? null : "base:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
