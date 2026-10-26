using System.IO;
using System.Windows;

namespace ReelCap;

public partial class App : Application
{
    /// <summary>"ReelCap.exe --selftest out.txt [exe]" records the primary monitor (3 s, system audio) and, if an exe
    /// name is given, that app's window with its own audio (6 s, the capture killed at 2.5 s to exercise resuming),
    /// writes what happened to out.txt next to the clips, and exits. Nothing is shown on screen.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0] == "--screenshot") { await Screenshot(e.Args[1], e.Args.Length > 2 && e.Args[2] == "app"); return; }
        if (e.Args.Length < 2 || e.Args[0] != "--selftest") { base.OnStartup(e); new MainWindow().Show(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = new StringWriter();
        var dir = Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!;
        log.WriteLine($"ffmpeg: {Ffmpeg.Path}");
        await Ffmpeg.ProbeAsync();
        log.WriteLine($"encoders: {string.Join(", ", Ffmpeg.Usable)}");
        foreach (var m in Monitors.Enumerate()) log.WriteLine($"monitor #{m.Number} {m.Title} {m.Detail} hmonitor={m.Handle} primary={m.Primary}");

        var primary = Monitors.Enumerate().First(m => m.Primary);
        await Record(log, new RecordOptions
        {
            Monitor = primary, Width = 1280, Height = 720, Fps = 60, Seconds = 3, SystemAudio = true,
            OutputPath = Path.Combine(dir, "selftest_screen.mp4"),
        }, killAt: 0);

        if (e.Args.Length > 2)
        {
            var app = AppWindows.Enumerate().FirstOrDefault(w => w.Exe.Equals(e.Args[2], StringComparison.OrdinalIgnoreCase));
            log.WriteLine(app == null ? $"no window for {e.Args[2]}" : $"window: {app.Title} pid={app.Pid} {app.Width}x{app.Height}");
            if (app != null)
                await Record(log, new RecordOptions
                {
                    Window = app, Width = app.Width & ~1, Height = app.Height & ~1, Fps = 60, Seconds = 6, AppAudio = true,
                    OutputPath = Path.Combine(dir, "selftest_app.mp4"),
                }, killAt: 2500);
        }
        File.WriteAllText(e.Args[1], log.ToString());
        Shutdown();
    }

    /// <summary>"--screenshot out.png [app]" renders the window off every monitor to a PNG and exits.</summary>
    async Task Screenshot(string path, bool appMode)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var w = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        w.Show();
        if (appMode) w.SelectAppMode();
        await Task.Delay(2500);
        w.UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(w);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var f = File.Create(path)) png.Save(f);
        w.Close();
        Shutdown();
    }

    static async Task Record(StringWriter log, RecordOptions o, int killAt)
    {
        log.WriteLine($"-- {Path.GetFileName(o.OutputPath)}");
        log.WriteLine("  args: " + Ffmpeg.BuildArgs(o, "seg.mkv", o.Seconds, 0));
        var rec = new Recorder();
        var done = new TaskCompletionSource<(bool, string)>();
        rec.Finished += (ok, msg) => done.TrySetResult((ok, msg));
        rec.Notice += n => log.WriteLine($"  notice: {n}");
        await rec.StartAsync(o);
        if (killAt > 0) { await Task.Delay(killAt); rec.SimulateCaptureLoss(); log.WriteLine($"  killed capture at {killAt} ms"); }
        var (success, message) = await done.Task;
        log.WriteLine($"  result: {success} {message}");
    }
}
