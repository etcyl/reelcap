using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReelCap;

public partial class MainWindow : Window
{
    /// <summary>Screenshot mode: made-up monitors and apps, nothing saved, nothing captured.</summary>
    public static bool DemoMode;

    readonly Settings _s = DemoMode ? new Settings { Folder = @"D:\Clips\ReelCap" } : Settings.Load();
    readonly Recorder _rec = new();
    readonly DispatcherTimer _thumbs = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Dictionary<MonitorInfo, Image> _thumbImages = new();
    List<MonitorInfo> _monitors = new();
    MonitorInfo? _monitor;
    List<AppWindow> _apps = new();
    AppWindow? _app;
    DateTime _started;
    int _recSeconds;
    string _lastClip = "";
    bool _stopping;

    TextBox _fpsBox = null!, _lengthBox = null!;
    WrapPanel _resChipsHost = null!;

    static readonly (string label, int height)[] Resolutions =
        { ("Native", 0), ("2160p", 2160), ("1440p", 1440), ("1080p", 1080), ("900p", 900), ("720p", 720) };
    static readonly int[] FpsOptions = { 30, 60, 120, 144, 240 };
    static readonly (string label, int seconds)[] Lengths =
        { ("Until I stop", 0), ("15 s", 15), ("30 s", 30), ("1 min", 60), ("2 min", 120), ("5 min", 300), ("10 min", 600) };

    public MainWindow()
    {
        InitializeComponent();
        _resChipsHost = ResolutionChips;
        BuildChips();
        CursorToggle.IsChecked = _s.Cursor;
        GpuToggle.IsChecked = _s.UseGpu;
        HideToggle.IsChecked = _s.HideWhileRecording;
        CursorToggle.Click += (_, _) => { _s.Cursor = CursorToggle.IsChecked == true; _s.Save(); };
        GpuToggle.Click += (_, _) => { _s.UseGpu = GpuToggle.IsChecked == true; _s.Save(); UpdateEncoderBadge(); };
        HideToggle.Click += (_, _) => { _s.HideWhileRecording = HideToggle.IsChecked == true; _s.Save(); };
        BindToggle(AppAudioToggle, () => _s.AppAudio, v => _s.AppAudio = v);
        BindToggle(OtherAudioToggle, () => _s.OtherAudio, v => _s.OtherAudio = v);
        BindToggle(SystemAudioToggle, () => _s.SystemAudio, v => _s.SystemAudio = v);
        BindToggle(MicToggle, () => _s.Mic, v => _s.Mic = v);
        FolderText.Text = _s.Folder;

        AddChip(SourceChips, "src", "Screen", !_s.AppMode, () => SetMode(false));
        AddChip(SourceChips, "src", "Application", _s.AppMode, () => SetMode(true));

        _rec.Progress += (frame, fps, drop, secs) => Dispatcher.BeginInvoke(() => OnProgress(frame, fps, drop, secs));
        _rec.Notice += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _rec.Finished += (ok, message) => Dispatcher.BeginInvoke(() => OnFinished(ok, message));
        _thumbs.Tick += (_, _) => RefreshThumbnails();
        _clock.Tick += (_, _) => Tick();

        Loaded += async (_, _) =>
        {
            LoadMonitors();
            LoadApps();
            SetMode(_s.AppMode);
            _thumbs.Start();
            if (Ffmpeg.Path == null)
            {
                EncoderText.Text = "ffmpeg not found";
                StatusText.Text = "ffmpeg.exe is missing. Put it next to ReelCap.exe or install it (winget install Gyan.FFmpeg).";
                RecordButton.IsEnabled = false;
                return;
            }
            await Ffmpeg.ProbeAsync();
            UpdateEncoderBadge();
        };
    }

    // ---------- UI building ----------

    void BuildChips()
    {
        foreach (var (label, h) in Resolutions)
            AddChip(ResolutionChips, "res", label, _s.Height == h, () => { _s.Height = h; _s.Save(); });

        foreach (var f in FpsOptions)
            AddChip(FpsChips, "fps", $"{f} fps", _s.Fps == f, () => { _s.Fps = f; _fpsBox.Text = ""; _s.Save(); });
        _fpsBox = AddBox(FpsChips, "Custom fps", FpsOptions.Contains(_s.Fps) ? "" : _s.Fps.ToString(), 100, text =>
        {
            if (int.TryParse(text, out var f) && f is >= 1 and <= 480) { _s.Fps = f; UncheckAll(FpsChips); _s.Save(); }
        });

        foreach (var (label, sec) in Lengths)
            AddChip(LengthChips, "len", label, _s.Seconds == sec, () => { _s.Seconds = sec; _lengthBox.Text = ""; _s.Save(); });
        _lengthBox = AddBox(LengthChips, "Custom (m:ss)", Lengths.Any(l => l.seconds == _s.Seconds) ? "" : FormatTime(_s.Seconds), 120, text =>
        {
            if (ParseDuration(text) is int s and > 0) { _s.Seconds = s; UncheckAll(LengthChips); _s.Save(); }
        });

        foreach (var q in Enum.GetValues<Quality>())
            AddChip(QualityChips, "q", q.ToString(), _s.Quality == q, () => { _s.Quality = q; _s.Save(); });
        AddChip(FormatChips, "fmt", "H.264", _s.Codec == Codec.H264, () => { _s.Codec = Codec.H264; _s.Save(); UpdateEncoderBadge(); });
        AddChip(FormatChips, "fmt", "HEVC", _s.Codec == Codec.Hevc, () => { _s.Codec = Codec.Hevc; _s.Save(); UpdateEncoderBadge(); });
        AddChip(FormatChips, "fmt", "AV1", _s.Codec == Codec.Av1, () => { _s.Codec = Codec.Av1; _s.Save(); UpdateEncoderBadge(); });
    }

    static RadioButton AddChip(Panel host, string group, string label, bool on, Action pick)
    {
        var rb = new RadioButton
        {
            Style = (Style)Application.Current.Resources["Chip"],
            GroupName = group + host.GetHashCode(), Content = label, IsChecked = on,
        };
        rb.Checked += (_, _) => pick();
        host.Children.Add(rb);
        return rb;
    }

    static TextBox AddBox(Panel host, string hint, string text, double width, Action<string> changed)
    {
        var tb = new TextBox { Style = (Style)Application.Current.Resources["ChipBox"], Tag = hint, Text = text, Width = width };
        tb.TextChanged += (_, _) => changed(tb.Text.Trim());
        host.Children.Add(tb);
        return tb;
    }

    void BindToggle(System.Windows.Controls.Primitives.ToggleButton t, Func<bool> get, Action<bool> set)
    {
        t.IsChecked = get();
        t.Click += (_, _) => { set(t.IsChecked == true); _s.Save(); };
    }

    /// <summary>For the off-screen screenshots: picks the first demo app, optionally shows a recording in progress.</summary>
    public void Demo(bool appMode, bool recording)
    {
        if (appMode)
        {
            ((RadioButton)SourceChips.Children[1]).IsChecked = true;
            ((RadioButton)AppList.Children[_apps.FindIndex(a => a.Exe == "SkyforgeArena")]).IsChecked = true;
            ((RadioButton)FpsChips.Children[3]).IsChecked = true;
            ((RadioButton)LengthChips.Children[3]).IsChecked = true;
            MicToggle.IsChecked = true;
        }
        if (!recording) return;
        _recSeconds = _s.Seconds;
        SetRecordingUi(true);
        TimerText.Text = "00:42" + (_recSeconds > 0 ? $"  /  {FormatTime(_recSeconds)}" : "");
        StatusText.Text = $"Recording {(appMode ? _app!.Exe : _monitor!.Title)}  ·  {Source()!.Value.h}p  ·  144 fps  ·  6048 frames";
        ProgressTrack.Visibility = _recSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        ProgressFill.Width = ProgressTrack.ActualWidth * 0.7;
    }

    void SetMode(bool app)
    {
        _s.AppMode = app;
        _s.Save();
        MonitorList.Visibility = app ? Visibility.Collapsed : Visibility.Visible;
        AppList.Visibility = app ? Visibility.Visible : Visibility.Collapsed;
        AppAudioToggle.Visibility = OtherAudioToggle.Visibility = app ? Visibility.Visible : Visibility.Collapsed;
        SystemAudioToggle.Visibility = app ? Visibility.Collapsed : Visibility.Visible;
        if (app) LoadApps();
        UpdateResolutionChips();
    }

    void LoadApps()
    {
        _apps = (DemoMode ? DemoData.Apps() : AppWindows.Enumerate()).OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Title).ToList();
        AppList.Children.Clear();
        _app = _apps.FirstOrDefault(a => _app != null && a.Hwnd == _app.Hwnd)
               ?? _apps.FirstOrDefault(a => a.Exe.Equals(_s.AppExe, StringComparison.OrdinalIgnoreCase));
        foreach (var a in _apps)
        {
            var row = new DockPanel();
            row.Children.Add(new Image { Source = a.Icon, Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(row.Children[0], Dock.Left);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = a.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = a.Detail, FontSize = 11.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            var card = new RadioButton
            {
                Style = (Style)FindResource("MonitorCard"), GroupName = "app", IsChecked = a == _app, Content = row,
                Margin = new Thickness(0, 0, 0, 8),
            };
            card.Checked += (_, _) => { _app = a; _s.AppExe = a.Exe; _s.Save(); UpdateResolutionChips(); };
            AppList.Children.Add(card);
        }
        if (_apps.Count == 0)
            AppList.Children.Add(new TextBlock { Text = "No application windows found.", Foreground = (Brush)FindResource("Muted") });
    }

    static void UncheckAll(Panel host)
    {
        foreach (var rb in host.Children.OfType<RadioButton>()) rb.IsChecked = false;
    }

    void LoadMonitors()
    {
        _monitors = DemoMode ? DemoData.Monitors() : Monitors.Enumerate();
        MonitorList.Children.Clear();
        _thumbImages.Clear();
        _monitor = _monitors.FirstOrDefault(m => m.DeviceName == _s.Monitor)
                   ?? _monitors.FirstOrDefault(m => m.Primary) ?? _monitors.FirstOrDefault();

        foreach (var m in _monitors)
        {
            var img = new Image { Stretch = Stretch.Uniform, Height = 92 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _thumbImages[m] = img;

            var title = new TextBlock { Text = m.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = m.Detail, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) };
            var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            badges.Children.Add(Badge($"{m.Number}"));
            if (m.Primary) badges.Children.Add(Badge("Primary"));

            var head = new Grid { Margin = new Thickness(2, 10, 2, 0) };
            head.Children.Add(new StackPanel { Children = { title, detail } });
            head.Children.Add(badges);

            var screen = new Border
            {
                CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Bg"), ClipToBounds = true, Child = img,
            };
            var card = new RadioButton
            {
                Style = (Style)FindResource("MonitorCard"), GroupName = "monitor", IsChecked = m == _monitor,
                Content = new StackPanel { Children = { screen, head } },
            };
            card.Checked += (_, _) => { _monitor = m; _s.Monitor = m.DeviceName; _s.Save(); UpdateResolutionChips(); };
            MonitorList.Children.Add(card);
        }
        if (_monitors.Count == 0)
            MonitorList.Children.Add(new TextBlock { Text = "No displays found.", Foreground = (Brush)FindResource("Muted") });
        RefreshThumbnails();
        UpdateResolutionChips();
    }

    Border Badge(string text) => new()
    {
        CornerRadius = new CornerRadius(5), Background = (Brush)FindResource("Line"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = (Brush)FindResource("Text") },
    };

    void RefreshThumbnails()
    {
        if (_rec.Running || WindowState == WindowState.Minimized) return;
        foreach (var (m, img) in _thumbImages)
            if ((DemoMode ? DemoData.Thumbnail(m) : Monitors.Thumbnail(m, 400)) is { } bmp) img.Source = bmp;
    }

    /// <summary>Width, height and name of what's being recorded: the picked window or monitor.</summary>
    (int w, int h, string name)? Source()
    {
        if (_s.AppMode)
        {
            if (_app == null) return null;
            if (DemoMode) return (_app.Width, _app.Height, _app.Exe);
            if (!AppWindows.Alive(_app.Hwnd)) return null;
            var (w, h) = AppWindows.ClientSize(_app.Hwnd);
            return (w, h, _app.Exe);
        }
        return _monitor == null ? null : (_monitor.Width, _monitor.Height, _monitor.Title);
    }

    /// <summary>Output frame size: the source scaled to the chosen height (never up), even sides.</summary>
    static (int w, int h) OutputSize(int srcW, int srcH, int height)
    {
        int h = height > 0 && height < srcH ? height : srcH;
        int w = (int)Math.Round(srcW * (double)h / srcH);
        return (w & ~1, h & ~1);
    }

    /// <summary>Resolutions above the source's height would upscale, so they're disabled.</summary>
    void UpdateResolutionChips()
    {
        if (Source() is not { } src) return;
        int i = 0;
        foreach (var rb in _resChipsHost.Children.OfType<RadioButton>())
        {
            int h = Resolutions[i++].height;
            rb.IsEnabled = h == 0 || h < src.h;
            rb.Content = h == 0 ? $"Native  ·  {src.h}p" : Resolutions[i - 1].label;
            if (!rb.IsEnabled && rb.IsChecked == true) ((RadioButton)_resChipsHost.Children[0]).IsChecked = true;
        }
    }

    void UpdateEncoderBadge()
    {
        if (Ffmpeg.Path == null) return;
        var (_, label) = Ffmpeg.Pick(_s.Codec, _s.UseGpu);
        EncoderText.Text = label;
        EncoderText.Foreground = (Brush)FindResource(label.StartsWith("CPU") ? "Muted" : "Good");
    }

    // ---------- Recording ----------

    async void Record_Click(object sender, RoutedEventArgs e) => await Toggle();

    string _recName = "";
    int _recHeight;

    async Task Toggle()
    {
        if (_stopping) return;
        if (_rec.Running) { await Stop(); return; }
        if (Ffmpeg.Path == null) return;
        if (Source() is not { } src)
        {
            StatusText.Text = _s.AppMode ? "Pick an application window to record." : "Pick a display to record.";
            return;
        }

        var (w, h) = OutputSize(src.w, src.h, _s.Height);
        var o = new RecordOptions
        {
            Monitor = _s.AppMode ? null : _monitor, Window = _s.AppMode ? _app : null,
            Width = w, Height = h, Fps = _s.Fps, Seconds = _s.Seconds,
            Codec = _s.Codec, UseGpu = _s.UseGpu, Quality = _s.Quality, Cursor = _s.Cursor,
            AppAudio = _s.AppMode && _s.AppAudio, OtherAudio = _s.AppMode && _s.OtherAudio,
            SystemAudio = !_s.AppMode && _s.SystemAudio, Mic = _s.Mic,
            OutputPath = Path.Combine(_s.Folder, $"ReelCap_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4"),
        };
        RecordButton.IsEnabled = false;
        try { await _rec.StartAsync(o); }
        catch (Exception ex) { StatusText.Text = ex.Message; RecordButton.IsEnabled = true; return; }
        RecordButton.IsEnabled = true;

        _recName = src.name;
        _recHeight = h;
        _recSeconds = o.Seconds;
        _started = DateTime.Now;
        _clock.Start();
        SetRecordingUi(true);
        StatusText.Text = $"Recording {_recName}  ·  {h}p  ·  {o.Fps} fps  ·  {Ffmpeg.Pick(o.Codec, o.UseGpu).label}";
        if (_s.HideWhileRecording) WindowState = WindowState.Minimized;
    }

    async Task Stop()
    {
        _stopping = true;
        StatusText.Text = "Finishing clip...";
        RecordButton.IsEnabled = false;
        await _rec.StopAsync();
    }

    void Tick()
    {
        var t = DateTime.Now - _started;
        TimerText.Text = FormatTime((int)t.TotalSeconds);
        if (_recSeconds > 0)
        {
            TimerText.Text += $"  /  {FormatTime(_recSeconds)}";
            var frac = Math.Clamp(t.TotalSeconds / _recSeconds, 0, 1);
            ProgressFill.Width = ProgressTrack.ActualWidth * frac;
        }
    }

    void OnProgress(long frame, double fps, long drop, double secs)
    {
        if (_stopping || !_rec.Running) return;
        StatusText.Text = $"Recording {_recName}  ·  {_recHeight}p  ·  {fps:0} fps" + (drop > 0 ? $"  ·  {drop} dropped" : "") + $"  ·  {frame} frames";
    }

    void OnFinished(bool ok, string message)
    {
        _clock.Stop();
        _stopping = false;
        SetRecordingUi(false);
        RecordButton.IsEnabled = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        if (ok && File.Exists(message))
        {
            _lastClip = message;
            var mb = new FileInfo(message).Length / (1024.0 * 1024.0);
            StatusText.Text = $"Saved {Path.GetFileName(message)}  ·  {mb:0.0} MB";
            StatusText.ToolTip = null;
            LastClipButton.Visibility = Visibility.Visible;
        }
        else
        {
            var line = message.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "unknown error";
            StatusText.Text = "Recording failed: " + line;
            StatusText.ToolTip = message;
        }
    }

    void SetRecordingUi(bool on)
    {
        RecordButton.Tag = on ? "rec" : null;
        TimerText.Foreground = (Brush)FindResource(on ? "Accent" : "Text");
        if (!on) TimerText.Text = "00:00";
        ProgressTrack.Visibility = on && _recSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressFill.Width = 0;
        foreach (UIElement el in new UIElement[] { MonitorList, AppList, SourceChips, SettingsPanel }) el.IsEnabled = !on;
    }

    // ---------- Buttons ----------

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void RefreshSources_Click(object sender, RoutedEventArgs e) { if (_rec.Running) return; LoadMonitors(); LoadApps(); UpdateResolutionChips(); }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_s.Folder) ? _s.Folder : null, Title = "Save clips to" };
        if (dlg.ShowDialog(this) != true) return;
        _s.Folder = dlg.FolderName;
        FolderText.Text = _s.Folder;
        _s.Save();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_s.Folder);
        if (File.Exists(_lastClip)) Process.Start("explorer.exe", $"/select,\"{_lastClip}\"");
        else Process.Start("explorer.exe", $"\"{_s.Folder}\"");
    }

    void LastClip_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_lastClip)) Process.Start(new ProcessStartInfo(_lastClip) { UseShellExecute = true });
    }

    // ---------- Helpers ----------

    static string FormatTime(int s) => s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";

    static int? ParseDuration(string text)
    {
        var parts = text.Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], out var sec)) return sec;
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s2)) return m * 60 + s2;
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m2) && int.TryParse(parts[2], out var s3))
            return h * 3600 + m2 * 60 + s3;
        return null;
    }

    // ---------- Window plumbing: global hotkey, rounded dark frame ----------

    const int HotkeyId = 0x5243;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE = round
        RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0001 | 0x4000, 0x52); // Ctrl+Alt+R, no repeat
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) { _ = Toggle(); handled = true; }
        return IntPtr.Zero;
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_rec.Running)
        {
            e.Cancel = true;
            await _rec.StopAsync();
            Close();
            return;
        }
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        base.OnClosing(e);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
