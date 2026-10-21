namespace Qadam1C;

internal static class AppIcon
{
    internal static readonly Icon Value = Load();

    private static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Qadam1C.AppIcon.ico")
            ?? throw new InvalidOperationException("Не найдена иконка Qadam1C.");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }
}
