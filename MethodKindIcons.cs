using System.Drawing.Drawing2D;

namespace Qadam1C;

internal static class MethodKindIcons
{
    internal static readonly Bitmap Procedure = Create(false);
    internal static readonly Bitmap Function = Create(true);
    internal static readonly Bitmap Region = CreateRegion();
    private static Bitmap CreateRegion()
    {
        var image = new Bitmap(18, 18);
        using var g = Graphics.FromImage(image);
        using var fill = new SolidBrush(Color.FromArgb(185, 137, 36));
        g.FillRectangle(fill, 2, 4, 6, 3);
        g.FillRectangle(fill, 2, 6, 14, 9);
        using var edge = new Pen(Color.FromArgb(225, 186, 92));
        g.DrawLine(edge, 3, 8, 15, 8);
        return image;
    }
    private static Bitmap Create(bool function)
    {
        var image = new Bitmap(18, 18);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(function ? Color.FromArgb(120, 76, 172) : Color.FromArgb(23, 126, 153));
        g.FillEllipse(fill, 1, 1, 16, 16);
        if (function)
        {
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("ƒ", font, text, new RectangleF(0, 0, 18, 17), format);
        }
        else
        {
            using var pen = new Pen(Color.White, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(7, 6), new PointF(4, 9), new PointF(7, 12)]);
            g.DrawLines(pen, [new PointF(11, 6), new PointF(14, 9), new PointF(11, 12)]);
        }
        return image;
    }
}
