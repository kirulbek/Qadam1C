using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Qadam1C;

internal static class TrayNotifications
{
    internal static string ErrorText(Exception error, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(error.Message)) return error.Message;
        return fallback + $" ({error.GetType().Name}, HRESULT 0x{error.HResult:X8})";
    }
    internal static void TryShow(Action<string, string> show, string? title, string? text)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Qadam1C" : title;
        text = string.IsNullOrWhiteSpace(text) ? "Не удалось выполнить команду Qadam1C." : text;
        try { show(title, text); }
        // A tray notification must not turn a handled application error into an unhandled UI exception.
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException or Win32Exception) { }
    }
    internal static void Show(NotifyIcon tray, int timeout, string? title, string? text, ToolTipIcon icon) =>
        TryShow((caption, message) => tray.ShowBalloonTip(timeout, caption, message, icon), title, text);
}
