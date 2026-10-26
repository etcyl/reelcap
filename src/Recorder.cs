using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReelCap;

public enum Codec { H264, Hevc }
public enum Quality { High, Balanced, Compact }

public sealed class RecordOptions
{
    public MonitorInfo Monitor = null!;
    public int Fps = 60;
    public int Height;              // 0 = native
    public int Seconds;             // 0 = until stopped
    public Codec Codec = Codec.H264;
    public bool UseGpu = true;
    public Quality Quality = Quality.High;
    public bool Cursor = true;
    public string OutputPath = "";
}

/// <summary>Finds ffmpeg and the hardware encoders it can actually open on this machine.</summary>
public static class Ffmpeg
{
    public static string? Path { get; } = Find();

    /// <summary>Usable encoder names, e.g. h264_nvenc, filled by <see cref="ProbeAsync"/>.</summary>
    public static HashSet<string> Usable { get; } = new();

    static string? Find()
    {
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        var winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            return Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        return null;
    }

    public static async Task ProbeAsync()
    {
        if (Path == null) return;
        string[] names = { "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf", "h264_qsv", "hevc_qsv" };
        var results = await Task.WhenAll(names.Select(async n =>
        {
            var psi = new ProcessStartInfo(Path, $"-hide_banner -loglevel error -f lavfi -i color=black:s=640x360:r=30 -frames:v 3 -pix_fmt nv12 -c:v {n} -f null -")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            try
            {
                using var p = Process.Start(psi)!;
                _ = p.StandardError.ReadToEndAsync();
                var done = p.WaitForExitAsync();
                if (await Task.WhenAny(done, Task.Delay(8000)) != done) { p.Kill(); return (n, false); }
                return (n, p.ExitCode == 0);
            }
            catch { return (n, false); }
        }));
        lock (Usable) foreach (var (n, ok) in results) if (ok) Usable.Add(n);
    }

    /// <summary>The encoder that will be used, and a label for it.</summary>
    public static (string name, string label) Pick(Codec codec, bool gpu)
    {
        var c = codec == Codec.H264 ? "h264" : "hevc";
        if (gpu)
        {
            lock (Usable)
            {
                if (Usable.Contains($"{c}_nvenc")) return ($"{c}_nvenc", "NVIDIA NVENC");
                if (Usable.Contains($"{c}_amf")) return ($"{c}_amf", "AMD AMF");
                if (Usable.Contains($"{c}_qsv")) return ($"{c}_qsv", "Intel Quick Sync");
            }
        }
        return codec == Codec.H264 ? ("libx264", "CPU (x264)") : ("libx265", "CPU (x265)");
    }

    public static string BuildArgs(RecordOptions o)
    {
        var (enc, _) = Pick(o.Codec, o.UseGpu);
        bool hw = !enc.StartsWith("lib");
        var m = o.Monitor;
        var sb = new StringBuilder("-hide_banner -nostats -loglevel error -progress pipe:1 -y ");
        var vf = new List<string>();

        if (m.DdaIndex >= 0)
        {
            sb.Append($"-f lavfi -i \"ddagrab=output_idx={m.DdaIndex}:framerate={o.Fps}:draw_mouse={(o.Cursor ? 1 : 0)}\" ");
            vf.Add("hwdownload");
            vf.Add("format=bgra");
        }
        else
        {
            sb.Append($"-f gdigrab -framerate {o.Fps} -draw_mouse {(o.Cursor ? 1 : 0)} -offset_x {m.X} -offset_y {m.Y} -video_size {m.Width}x{m.Height} -i desktop ");
        }

        if (o.Height > 0 && o.Height < m.Height) vf.Add($"scale=-2:{o.Height}:flags=lanczos");
        vf.Add(hw ? "format=nv12" : "format=yuv420p");
        sb.Append($"-vf \"{string.Join(",", vf)}\" ");

        int q = o.Quality switch { Quality.High => 19, Quality.Balanced => 24, _ => 29 };
        if (o.Codec == Codec.Hevc) q += 1;
        sb.Append($"-c:v {enc} ");
        sb.Append(enc switch
        {
            var e when e.EndsWith("_nvenc") => $"-preset p5 -tune hq -rc vbr -cq {q} -b:v 0 ",
            var e when e.EndsWith("_amf") => $"-quality quality -rc cqp -qp_i {q} -qp_p {q} -qp_b {q} ",
            var e when e.EndsWith("_qsv") => $"-preset medium -global_quality {q} ",
            "libx264" => $"-preset {(o.Fps > 60 ? "superfast" : "veryfast")} -crf {q} ",
            _ => $"-preset {(o.Fps > 60 ? "superfast" : "veryfast")} -crf {q + 2} ",
        });
        if (o.Codec == Codec.Hevc) sb.Append("-tag:v hvc1 ");
        sb.Append($"-g {o.Fps * 2} ");
        if (o.Seconds > 0) sb.Append($"-t {o.Seconds} ");
        sb.Append($"-movflags +faststart \"{o.OutputPath}\"");
        return sb.ToString();
    }
}

/// <summary>One ffmpeg capture process. Stop sends 'q' so the MP4 is finalised.</summary>
public sealed class Recorder
{
    Process? _proc;
    readonly Queue<string> _errors = new();
    readonly Dictionary<string, string> _progress = new();

    public bool Running => _proc is { HasExited: false };
    public string OutputPath { get; private set; } = "";

    /// <summary>frame, fps, dropped, seconds recorded.</summary>
    public event Action<long, double, long, double>? Progress;
    /// <summary>Exit code and the last error lines.</summary>
    public event Action<int, string>? Finished;

    public void Start(RecordOptions o)
    {
        if (Ffmpeg.Path == null) throw new InvalidOperationException("ffmpeg.exe was not found.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(o.OutputPath)!);
        OutputPath = o.OutputPath;
        _errors.Clear();
        var psi = new ProcessStartInfo(Ffmpeg.Path, Ffmpeg.BuildArgs(o))
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnProgressLine(e.Data);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_errors) { _errors.Enqueue(e.Data); while (_errors.Count > 12) _errors.Dequeue(); }
        };
        p.Exited += (_, _) =>
        {
            string err; lock (_errors) err = string.Join("\n", _errors);
            Finished?.Invoke(p.ExitCode, err);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
    }

    public async Task StopAsync()
    {
        var p = _proc;
        if (p == null || p.HasExited) return;
        try { await p.StandardInput.WriteAsync('q'); await p.StandardInput.FlushAsync(); } catch { }
        var done = p.WaitForExitAsync();
        if (await Task.WhenAny(done, Task.Delay(10000)) != done) { try { p.Kill(); } catch { } }
    }

    public void Kill() { try { if (Running) _proc!.Kill(); } catch { } }

    void OnProgressLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        int eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line[..eq];
        var val = line[(eq + 1)..].Trim();
        if (key != "progress") { _progress[key] = val; return; }
        long.TryParse(_progress.GetValueOrDefault("frame"), out var frame);
        double.TryParse(_progress.GetValueOrDefault("fps"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps);
        long.TryParse(_progress.GetValueOrDefault("drop_frames"), out var drop);
        long.TryParse(_progress.GetValueOrDefault("out_time_us"), out var us);
        Progress?.Invoke(frame, fps, drop, Math.Max(0, us / 1e6));
    }
}
