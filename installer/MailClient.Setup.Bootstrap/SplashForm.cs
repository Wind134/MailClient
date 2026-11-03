using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MailClient.Setup.Bootstrap
{
    /// <summary>解压期间的启动画面：圆角小窗 + 图标 + 进度条，跟随系统深浅色。解压完成、安装窗口出现后自动消失。</summary>
    internal sealed class SplashForm : Form
    {
        private readonly bool _dark = IsDark();
        private readonly Image _icon;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private double _target;
        private double _shown;
        private float _pulse;

        public SplashForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Text = "邮件 安装程序";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.png"))
            {
                _icon = s == null ? null : Image.FromStream(s);
            }

            ClientSize = new Size(Scale(440), Scale(132));
            BackColor = _dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF3, 0xF3, 0xF3);
            _timer.Tick += (s, e) =>
            {
                _shown += (_target - _shown) * 0.15;
                _pulse = (_pulse + 0.03f) % 6.2832f;
                Invalidate();
            };
            _timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        public void SetProgress(double fraction)
        {
            _target = Math.Max(0, Math.Min(1, fraction));
        }

        public void CloseFromWorker()
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    _timer.Stop();
                    if (!IsDisposed)
                    {
                        Close();
                    }
                }));
            }
            catch (Exception)
            {
                // 窗口已经关了
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                var pref = 2; // DWMWCP_ROUND：Windows 11 系统圆角
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch (Exception)
            {
                // 旧系统没有圆角，保持直角
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var fore = _dark ? Color.White : Color.FromArgb(0x1A, 0x1A, 0x1A);
            var sub = _dark ? Color.FromArgb(0xB0, 0xB0, 0xB0) : Color.FromArgb(0x60, 0x60, 0x60);

            var iconSize = Scale(52) + (int)(Math.Sin(_pulse) * Scale(2));
            var iconX = Scale(28) - ((iconSize - Scale(52)) / 2);
            var iconY = Scale(28) - ((iconSize - Scale(52)) / 2);
            if (_icon != null)
            {
                g.DrawImage(_icon, new Rectangle(iconX, iconY, iconSize, iconSize));
            }

            using (var titleFont = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold))
            using (var subFont = new Font("Microsoft YaHei UI", 9f))
            using (var titleBrush = new SolidBrush(fore))
            using (var subBrush = new SolidBrush(sub))
            {
                g.DrawString("正在准备「邮件」安装程序…", titleFont, titleBrush, Scale(96), Scale(30));
                g.DrawString("首次启动需要解压文件，请稍候", subFont, subBrush, Scale(97), Scale(58));
            }

            var barX = Scale(28);
            var barW = ClientSize.Width - Scale(56);
            var barH = Math.Max(4, Scale(5));
            var barY = ClientSize.Height - Scale(34);
            using (var track = new SolidBrush(_dark ? Color.FromArgb(0x48, 0x48, 0x48) : Color.FromArgb(0xD8, 0xD8, 0xD8)))
            {
                FillRound(g, track, new Rectangle(barX, barY, barW, barH));
            }

            var fill = (int)(barW * _shown);
            if (fill > barH)
            {
                var rect = new Rectangle(barX, barY, fill, barH);
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(0x1E, 0x7B, 0xFF), Color.FromArgb(0x2B, 0x3F, 0xD1), 0f))
                {
                    FillRound(g, brush, rect);
                }
            }

            using (var border = new Pen(_dark ? Color.FromArgb(0x55, 0x55, 0x55) : Color.FromArgb(0xC8, 0xC8, 0xC8)))
            {
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        private static void FillRound(Graphics g, Brush brush, Rectangle r)
        {
            using (var path = new GraphicsPath())
            {
                var d = r.Height;
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        private int Scale(int value)
        {
            return (int)Math.Round(value * DeviceDpi / 96.0);
        }

        private static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
