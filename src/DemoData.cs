using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReelCap;

/// <summary>Made-up monitors and windows for screenshots (--screenshot), so they never show a real desktop.</summary>
public static class DemoData
{
    public static List<MonitorInfo> Monitors() => new()
    {
        new() { FriendlyName = "Side Display", Width = 1920, Height = 1080, RefreshHz = 60, Number = 1, DeviceName = "demo1" },
        new() { FriendlyName = "Gaming Monitor", Width = 2560, Height = 1440, RefreshHz = 240, Number = 2, Primary = true, DeviceName = "demo2" },
        new() { FriendlyName = "Studio Display", Width = 3840, Height = 2160, RefreshHz = 120, Number = 3, DeviceName = "demo3" },
    };

    public static List<AppWindow> Apps() => new()
    {
        App("Skyforge Arena", "SkyforgeArena", 2560, 1440, "S", "#FF4A5E"),
        App("Untitled - Notepad", "notepad", 1280, 820, "N", "#3B82F6"),
        App("Downloads - File Explorer", "explorer", 1600, 960, "E", "#F59E0B"),
        App("Track Editor", "audiotool", 1440, 900, "T", "#10B981"),
    };

    static AppWindow App(string title, string exe, int w, int h, string letter, string color) =>
        new() { Title = title, Exe = exe, Width = w, Height = h, Hwnd = new IntPtr(title.GetHashCode() & 0x7FFFFFFF), Icon = Icon(letter, color) };

    static BitmapSource Icon(string letter, string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        return Render(32, 32, dc =>
        {
            dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, 32, 32), 8, 8);
            var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 18, Brushes.White, 1.0);
            dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
        });
    }

    /// <summary>A stylised desktop: wallpaper gradient, a couple of windows and a taskbar.</summary>
    public static BitmapSource Thumbnail(MonitorInfo m)
    {
        const int w = 400;
        int h = w * m.Height / m.Width;
        (string a, string b) = m.Number switch { 1 => ("#1E3A5F", "#0B1220"), 2 => ("#3B1E4A", "#120A1A"), _ => ("#1F4D3A", "#08140F") };
        return Render(w, h, dc =>
        {
            var bg = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 45);
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            var win = new SolidColorBrush(Color.FromArgb(235, 28, 31, 40));
            var bar = new SolidColorBrush(Color.FromArgb(255, 40, 44, 56));
            dc.DrawRoundedRectangle(win, null, new Rect(w * 0.08, h * 0.12, w * 0.5, h * 0.55), 6, 6);
            dc.DrawRectangle(bar, null, new Rect(w * 0.08, h * 0.12, w * 0.5, h * 0.06));
            dc.DrawRoundedRectangle(win, null, new Rect(w * 0.5, h * 0.3, w * 0.42, h * 0.5), 6, 6);
            dc.DrawRectangle(bar, null, new Rect(w * 0.5, h * 0.3, w * 0.42, h * 0.06));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 18, 20, 26)), null, new Rect(0, h - h * 0.07, w, h * 0.07));
        });
    }

    static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }
}
