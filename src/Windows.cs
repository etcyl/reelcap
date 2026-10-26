using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ReelCap;

public sealed class AppWindow
{
    public IntPtr Hwnd;
    public int Pid;
    public string Title = "";
    public string Exe = "";          // e.g. Crossguard (no extension)
    public int Width, Height;        // client area, physical pixels
    public BitmapSource? Icon;

    public string Detail => $"{Exe}.exe  ·  {Width} x {Height}";
}

/// <summary>Top-level application windows that can be captured.</summary>
public static class AppWindows
{
    public static List<AppWindow> Enumerate()
    {
        var list = new List<AppWindow>();
        int self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */).ToInt64() & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self) return true;
            GetClientRect(hwnd, out var rc);
            if (rc.Right - rc.Left < 64 || rc.Bottom - rc.Top < 64) return true;
            string exe;
            try { exe = Process.GetProcessById(pid).ProcessName; } catch { return true; }
            if (exe is "explorer" && sb.ToString() == "Program Manager") return true;
            if (exe is "TextInputHost" or "ApplicationFrameHost" or "SystemSettings" && sb.Length == 0) return true;
            list.Add(new AppWindow
            {
                Hwnd = hwnd, Pid = pid, Title = sb.ToString(), Exe = exe,
                Width = rc.Right - rc.Left, Height = rc.Bottom - rc.Top, Icon = IconOf(hwnd),
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool Alive(IntPtr hwnd) => IsWindow(hwnd);

    public static (int w, int h) ClientSize(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var rc);
        return (rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    static BitmapSource? IconOf(IntPtr hwnd)
    {
        var h = SendMessageTimeout(hwnd, 0x7F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 2, 100, out var icon) != IntPtr.Zero ? icon : IntPtr.Zero;
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
