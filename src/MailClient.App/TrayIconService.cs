using System.Runtime.InteropServices;

namespace MailClient_App;

/// <summary>
/// 打包 WinUI 3 应用的通知区域图标（Shell_NotifyIcon）。
/// Windows App SDK 没有一等托盘 API；用 Win32 消息窗口收回调，右键用系统弹出菜单。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint WmTrayIcon = 0x8000 + 1; // WM_APP + 1
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const uint WmNull = 0x0000;
    private const uint NinSelect = 0x0400;      // WM_USER
    private const uint NinKeySelect = 0x0401;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int IdOpen = 1;
    private const int IdSync = 2;
    private const int IdExit = 3;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProc _wndProc;
    private readonly string _className;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;
    private string _tip = "邮件";

    public event Action? OpenRequested;
    public event Action? SyncAllRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _wndProc = WndProcImpl;
        _className = "MailClientTrayMsgWnd_" + Guid.NewGuid().ToString("N");
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        }

        _hwnd = CreateWindowEx(0, _className, "MailClientTray", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        }
    }

    public bool IsVisible => _added;

    /// <summary>在通知区域显示图标。可重复调用以更新提示文字。</summary>
    public void Show(string? iconPath, string tip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tip = string.IsNullOrWhiteSpace(tip) ? "邮件" : tip.Trim();
        EnsureIcon(iconPath);

        var data = BuildData();
        if (_added)
        {
            Shell_NotifyIcon(NimModify, ref data);
            return;
        }

        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            // 偶发失败：再试一次
            Thread.Sleep(50);
            data = BuildData();
            Shell_NotifyIcon(NimAdd, ref data);
        }

        _added = true;
    }

    public void Hide()
    {
        if (!_added || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = BuildData();
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { Hide(); } catch { /* ignore */ }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClass(_className, GetModuleHandle(null));
    }

    private void EnsureIcon(string? iconPath)
    {
        if (_hIcon != IntPtr.Zero)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            _hIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        }

        if (_hIcon == IntPtr.Zero)
        {
            // 回退：系统应用程序图标
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)0x7F00); // IDI_APPLICATION
        }
    }

    private NotifyIconData BuildData()
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = _hIcon,
            szTip = _tip.Length <= 127 ? _tip : _tip[..127]
        };
        return data;
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayIcon)
        {
            var eventCode = (uint)lParam.ToInt64() & 0xFFFF;
            switch (eventCode)
            {
                case NinSelect:
                case NinKeySelect:
                case WmLButtonDblClk:
                    OpenRequested?.Invoke();
                    break;
                case WmRButtonUp:
                case WmContextMenu:
                    ShowContextMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            switch ((int)wParam & 0xFFFF)
            {
                case IdOpen: OpenRequested?.Invoke(); break;
                case IdSync: SyncAllRequested?.Invoke(); break;
                case IdExit: ExitRequested?.Invoke(); break;
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var pt))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, (UIntPtr)IdOpen, "打开");
            AppendMenu(menu, MfString, (UIntPtr)IdSync, "同步全部");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, string.Empty);
            AppendMenu(menu, MfString, (UIntPtr)IdExit, "退出");

            // 系统要求：弹出菜单前把窗口设为前台，否则点别处不会自动收起
            SetForegroundWindow(_hwnd);
            var cmd = (int)TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0)
            {
                switch (cmd)
                {
                    case IdOpen: OpenRequested?.Invoke(); break;
                    case IdSync: SyncAllRequested?.Invoke(); break;
                    case IdExit: ExitRequested?.Invoke(); break;
                }
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ───────── Win32 ─────────

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
