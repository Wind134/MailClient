using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MailClient_App;

/// <summary>
/// 给左栏账号节点生成「彩色圆底 + 白色首字母」图标。
/// NavigationViewItem.Icon 只接受 IconElement，不能放 Border/TextBlock，
/// 所以用 GDI+ 画一张小位图，再以 ImageIcon 的形式给它。
/// </summary>
internal static class LetterAvatar
{
    private const int LogicalSize = 24;
    private static readonly Dictionary<(string Letter, string Hex), WriteableBitmap> Cache = new();

    public static ImageIcon Create(string letter, Windows.UI.Color color, double scale = 2.0)
    {
        var key = (letter, $"{color.R:X2}{color.G:X2}{color.B:X2}@{scale:0.##}");
        if (!Cache.TryGetValue(key, out var bitmap))
        {
            bitmap = Render(letter, color, scale);
            Cache[key] = bitmap;
        }

        return new ImageIcon { Source = bitmap, Width = LogicalSize, Height = LogicalSize };
    }

    private static WriteableBitmap Render(string letter, Windows.UI.Color color, double scale)
    {
        var px = (int)Math.Ceiling(LogicalSize * scale);
        using var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(color.R, color.G, color.B));
            g.FillEllipse(brush, 0.5f, 0.5f, px - 1, px - 1);

            using var font = new Font("Microsoft YaHei UI", px * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            // GenericTypographic 的行高偏紧，字形视觉上略偏上，向下补一点
            var rect = new RectangleF(0, px * 0.03f, px, px);
            g.DrawString(letter, font, Brushes.White, rect, format);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[data.Stride * px];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var wb = new WriteableBitmap(px, px);
            bytes.CopyTo(0, wb.PixelBuffer, 0, bytes.Length);
            wb.Invalidate();
            return wb;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
