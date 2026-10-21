namespace Qadam1C;

// Shared, application-lifetime fonts: identifiers lead, supporting UI stays quiet.
internal static class PanelTypography
{
    internal static readonly Font Identifier = new("Consolas", 10);
    internal static readonly Font Secondary = new("Segoe UI", 8.5f);
    internal static readonly Font Caption = new("Segoe UI", 8);
    internal static readonly Font Signature = new("Consolas", 8.5f);
    internal static readonly Font RegionHeading = new("Segoe UI", 9, FontStyle.Bold);
    internal static readonly Color Muted = Color.FromArgb(91, 105, 122);
    internal static readonly Color Primary = Color.FromArgb(29, 44, 64);
}
