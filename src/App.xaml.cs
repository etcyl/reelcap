using System.IO;
using System.Windows;

namespace ReelCap;

public partial class App : Application
{
    /// <summary>"ReelCap.exe --selftest out.txt" lists displays and encoders, records 2 s of each display and exits.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length < 2 || e.Args[0] != "--selftest") { base.OnStartup(e); new MainWindow().Show(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = new StringWriter();
        log.WriteLine($"ffmpeg: {Ffmpeg.Path}");
        await Ffmpeg.ProbeAsync();
        log.WriteLine($"encoders: {string.Join(", ", Ffmpeg.Usable)}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!;
        foreach (var m in Monitors.Enumerate())
        {
            log.WriteLine($"#{m.Number} {m.Title} {m.DeviceName} at {m.X},{m.Y} {m.Detail} dda={m.DdaIndex} primary={m.Primary}");
            var o = new RecordOptions { Monitor = m, Fps = 60, Height = 720, Seconds = 2, OutputPath = Path.Combine(dir, $"selftest_{m.Number}.mp4") };
            log.WriteLine("  args: " + Ffmpeg.BuildArgs(o));
            var rec = new Recorder();
            var done = new TaskCompletionSource<(int, string)>();
            rec.Finished += (c, err) => done.TrySetResult((c, err));
            rec.Start(o);
            var (code, error) = await done.Task;
            log.WriteLine($"  exit {code}, {(File.Exists(o.OutputPath) ? new FileInfo(o.OutputPath).Length : 0)} bytes {error}");
        }
        File.WriteAllText(e.Args[1], log.ToString());
        Shutdown();
    }
}
