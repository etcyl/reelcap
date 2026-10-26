using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReelCap;

public enum Codec { H264, Hevc, Av1 }
public enum Quality { High, Balanced, Compact }

public sealed class RecordOptions
{
    public MonitorInfo? Monitor;    // one of Monitor / Window
    public AppWindow? Window;
    public int Width, Height;       // output frame size (even)
    public int Fps = 60;
    public int Seconds;             // 0 = until stopped
    public Codec Codec = Codec.H264;
    public bool UseGpu = true;
    public Quality Quality = Quality.High;
    public bool Cursor = true;
    public bool AppAudio, OtherAudio, SystemAudio, Mic;
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
        string[] names = { "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_amf", "hevc_amf", "av1_amf", "h264_qsv", "hevc_qsv", "av1_qsv" };
        var results = await Task.WhenAll(names.Select(async n => (n, await Run($"-hide_banner -loglevel error -f lavfi -i color=black:s=640x360:r=30 -frames:v 3 -pix_fmt nv12 -c:v {n} -f null -", 8000) == 0)));
        lock (Usable) foreach (var (n, ok) in results) if (ok) Usable.Add(n);
    }

    /// <summary>Runs ffmpeg to completion; returns the exit code (-1 on timeout).</summary>
    public static async Task<int> Run(string args, int timeoutMs, StringBuilder? stderr = null)
    {
        var psi = new ProcessStartInfo(Path!, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var done = p.WaitForExitAsync();
            if (await Task.WhenAny(done, Task.Delay(timeoutMs)) != done) { p.Kill(); return -1; }
            stderr?.Append(await err);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static string CodecPrefix(Codec c) => c switch { Codec.H264 => "h264", Codec.Hevc => "hevc", _ => "av1" };

    /// <summary>The encoder that will be used, and a label for it.</summary>
    public static (string name, string label) Pick(Codec codec, bool gpu)
    {
        var c = CodecPrefix(codec);
        if (gpu)
        {
            lock (Usable)
            {
                if (Usable.Contains($"{c}_nvenc")) return ($"{c}_nvenc", "NVIDIA NVENC");
                if (Usable.Contains($"{c}_amf")) return ($"{c}_amf", "AMD AMF");
                if (Usable.Contains($"{c}_qsv")) return ($"{c}_qsv", "Intel Quick Sync");
            }
        }
        return codec switch
        {
            Codec.H264 => ("libx264", "CPU (x264)"),
            Codec.Hevc => ("libx265", "CPU (x265)"),
            _ => ("libsvtav1", "CPU (SVT-AV1)"),
        };
    }


    /// <summary>One video capture run. Frames keep the capture's own QPC spacing; the first frame's wall-clock
    /// time relative to <paramref name="baseUnixUs"/> (the recording's time zero, shared with the audio) is added
    /// once, so every segment lands at its true place on the recording's timeline. With NVENC the captured D3D11
    /// textures go straight into the encoder (it converts BGRA to YUV on the GPU with the BT.601 matrix, tagged as
    /// such); other encoders get frames copied to system memory.</summary>
    public static string BuildArgs(RecordOptions o, string output, double seconds, long baseUnixUs)
    {
        var (enc, _) = Pick(o.Codec, o.UseGpu);
        var sb = new StringBuilder("-hide_banner -nostats -loglevel error -progress pipe:1 -y ");

        var src = o.Window != null ? $"hwnd={o.Window.Hwnd.ToInt64()}" : $"hmonitor={o.Monitor!.Handle.ToInt64()}";
        sb.Append($"-probesize 32 -analyzeduration 0 -f lavfi -i \"gfxcapture={src}:max_framerate={o.Fps}:capture_cursor={(o.Cursor ? 1 : 0)}" +
                  $":width={o.Width}:height={o.Height}:resize_mode=scale_aspect:scale_mode=bicubic\" ");

        bool zeroCopy = enc.EndsWith("_nvenc");
        // ffmpeg rebases every output to zero, so the first frame's offset from time zero is logged (at error
        // level, so it shows through -loglevel error) and read back by the recorder.
        var place = $"setpts='PTS+0*if(eq(N,0),print(RTCTIME-{baseUnixUs},16),0)'";
        // Both paths convert with the BT.601 matrix (NVENC internally, swscale by default); the capture tags its
        // frames as RGB, so the tags are set explicitly or players would misread the colours.
        var download = $",hwdownload,format=bgra,format={(enc.StartsWith("lib") ? "yuv420p" : "nv12")}";
        var tags = ",setparams=colorspace=smpte170m:color_primaries=bt709:color_trc=bt709:range=tv";
        sb.Append($"-filter:v \"{place}{(zeroCopy ? "" : download)}{tags}\" ");
        sb.Append($"-fps_mode cfr -r {o.Fps} ");

        int q = o.Quality switch { Quality.High => 19, Quality.Balanced => 24, _ => 29 };
        if (o.Codec != Codec.H264) q += 1;
        sb.Append($"-c:v {enc} ");
        sb.Append(enc switch
        {
            var e when e.EndsWith("_nvenc") => $"-preset p5 -tune hq -rc vbr -cq {q} -b:v 0 ",
            var e when e.EndsWith("_amf") => $"-quality quality -rc cqp -qp_i {q} -qp_p {q} -qp_b {q} ",
            var e when e.EndsWith("_qsv") => $"-preset medium -global_quality {q} ",
            "libsvtav1" => $"-preset 10 -crf {q + 8} ",
            _ => $"-preset {(o.Fps > 60 ? "superfast" : "veryfast")} -crf {q} ",
        });
        sb.Append($"-g {o.Fps * 2} -flush_packets 1 -cluster_time_limit 1000 ");
        if (seconds > 0) sb.Append($"-t {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        sb.Append($"\"{output}\"");
        return sb.ToString();
    }
}

/// <summary>A recording. Audio is one continuous track for the whole recording (<see cref="AudioMixer"/>). Video is
/// one or more capture runs (segments): if the capture drops mid-recording (a game switching display mode, a UAC
/// prompt) a new segment starts at once. Every segment is stamped against the same time zero as the audio, so at
/// stop they are placed on the timeline (a short freeze where capture was lost) and muxed with the audio, no re-encode.</summary>
public sealed class Recorder
{
    RecordOptions _o = null!;
    AudioMixer? _mixer;
    Process? _proc;
    Task? _session;
    volatile bool _stop;
    long _base100ns, _baseUnixUs;
    readonly Dictionary<string, string> _progress = new();
    readonly Queue<string> _errors = new();

    public bool Running => _session is { IsCompleted: false };

    /// <summary>frames, fps, dropped, seconds recorded so far.</summary>
    public event Action<long, double, long, double>? Progress;
    public event Action<string>? Notice;
    /// <summary>Success and the saved file, or failure and the error text.</summary>
    public event Action<bool, string>? Finished;

    public async Task StartAsync(RecordOptions o)
    {
        if (Ffmpeg.Path == null) throw new InvalidOperationException("ffmpeg.exe was not found.");
        if (Running) return;
        _o = o;
        _stop = false;
        Directory.CreateDirectory(TempDir);

        var inputs = new List<AudioInput>();
        var problems = new List<string>();
        async Task Add(Func<Task<AudioInput>> make, string what)
        {
            try { inputs.Add(await make()); } catch (Exception ex) { problems.Add($"{what} unavailable ({ex.Message})"); }
        }
        int pid = o.Window?.Pid ?? 0;
        if (o.AppAudio && pid != 0) await Add(() => AudioInput.Process(pid, exclude: false), "App audio");
        if (o.OtherAudio && pid != 0) await Add(() => AudioInput.Process(pid, exclude: true), "Other apps' audio");
        if (o.SystemAudio) await Add(() => Task.FromResult(AudioInput.System()), "System audio");
        if (o.Mic) await Add(() => Task.FromResult(AudioInput.Microphone()), "Microphone");
        if (problems.Count > 0) Notice?.Invoke(string.Join("; ", problems));

        // Time zero for everything: QPC for the audio, the same instant in Unix microseconds for the video.
        _base100ns = AudioFormat.Now100ns();
        _baseUnixUs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        _mixer = inputs.Count > 0 ? new AudioMixer(inputs, System.IO.Path.Combine(TempDir, "audio.mka")) : null;
        _mixer?.Start(_base100ns);
        _session = Task.Run(Session);
    }

    string TempDir => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_o.OutputPath)!,
        ".reelcap_" + System.IO.Path.GetFileNameWithoutExtension(_o.OutputPath));

    double Elapsed => (AudioFormat.Now100ns() - _base100ns) / 1e7;

    public async Task StopAsync()
    {
        _stop = true;
        await SendQuit();
        if (_session != null) await _session;
    }

    /// <summary>Kills the current capture run as a display mode switch would (self-test).</summary>
    public void SimulateCaptureLoss() { try { _proc?.Kill(); } catch { } }

    async Task SendQuit()
    {
        var p = _proc;
        if (p == null || p.HasExited) return;
        try { await p.StandardInput.WriteAsync('q'); await p.StandardInput.FlushAsync(); } catch { }
    }

    async Task Session()
    {
        var tmp = TempDir;
        var segments = new List<string>();
        var starts = new List<double>();
        string lastError = "";
        int failsInRow = 0, attempt = 0;

        while (!_stop)
        {
            double remaining = 0;
            if (_o.Seconds > 0)
            {
                // Fixed length is measured on the recording's clock, so time lost to a restart counts.
                remaining = _o.Seconds - Elapsed;
                if (remaining < 0.1) break;
            }
            if (_o.Window != null && !AppWindows.Alive(_o.Window.Hwnd)) break;

            var seg = System.IO.Path.Combine(tmp, $"video{attempt++:000}.mkv");
            var (code, frames, err, start) = await RunSegment(seg, remaining);
            if (frames > 0 && start is { } at && await StartTime(seg) != null) { segments.Add(seg); starts.Add(Math.Max(0, at)); failsInRow = 0; }
            else { TryDelete(seg); failsInRow++; lastError = err; }

            if (_stop) break;
            if (code == 0 && _o.Seconds > 0) break;              // reached the length
            if (segments.Count == 0 && failsInRow > 0) break;   // never got going: a real error, not a mode switch
            if (failsInRow > 40) break;                         // capture gone for good
            if (_o.Window != null && !AppWindows.Alive(_o.Window.Hwnd)) break;
            Notice?.Invoke(failsInRow == 0 ? "Capture interrupted, resuming..." : "Waiting for the capture to come back...");
            await Task.Delay(failsInRow == 0 ? 50 : 500);
        }

        var stopAt = AudioFormat.Now100ns();
        if (_o.Seconds > 0) stopAt = Math.Min(stopAt, _base100ns + _o.Seconds * 10_000_000L);
        Notice?.Invoke("Saving...");
        if (_mixer != null) await _mixer.StopAsync(stopAt);

        if (segments.Count == 0)
        {
            TryDeleteDir(tmp);
            Finished?.Invoke(false, string.IsNullOrWhiteSpace(lastError) ? "Nothing was captured." : lastError);
            return;
        }

        var (ok, error) = await Mux(segments, starts, tmp);
        if (ok && Environment.GetEnvironmentVariable("REELCAP_KEEP") == null) TryDeleteDir(tmp);
        Finished?.Invoke(ok, ok ? _o.OutputPath : $"Could not save the clip ({error}). The parts are in {tmp}");
    }

    /// <summary>Runs one capture; returns its exit code, frame count, errors and where its first frame sits on the
    /// recording's timeline (seconds).</summary>
    async Task<(int code, long frames, string err, double? start)> RunSegment(string output, double seconds)
    {
        lock (_errors) _errors.Clear();
        _progress.Clear();
        long frames = 0;
        double? offsetUs = null;
        var psi = new ProcessStartInfo(Ffmpeg.Path!, Ffmpeg.BuildArgs(_o, output, seconds, _baseUnixUs))
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (ParseProgress(e.Data) is { } pr)
            {
                frames = pr.frame;
                Progress?.Invoke(pr.frame, pr.fps, pr.drop, Elapsed);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (e.Data.StartsWith("[Eval"))
            {
                if (offsetUs == null && double.TryParse(e.Data[(e.Data.LastIndexOf(' ') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var us))
                    offsetUs = us;
                return;
            }
            lock (_errors) { _errors.Enqueue(e.Data); while (_errors.Count > 12) _errors.Dequeue(); }
        };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        if (_stop) await SendQuit();

        await p.WaitForExitAsync();
        _proc = null;
        string err; lock (_errors) err = string.Join("\n", _errors);
        if (frames == 0 && File.Exists(output) && new FileInfo(output).Length > 4096) frames = 1; // ended before the first progress report
        return (p.ExitCode, frames, err, offsetUs / 1e6);
    }

    (long frame, double fps, long drop)? ParseProgress(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq];
        var val = line[(eq + 1)..].Trim();
        if (key != "progress") { _progress[key] = val; return null; }
        long.TryParse(_progress.GetValueOrDefault("frame"), out var frame);
        double.TryParse(_progress.GetValueOrDefault("fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps);
        long.TryParse(_progress.GetValueOrDefault("drop_frames"), out var drop);
        return (frame, fps, drop);
    }

    /// <summary>Places the video segments at their recorded start times (each file's concat duration runs to the
    /// next segment's start, so a lost stretch holds the last frame) and muxes them with the audio track into
    /// the final MP4, without re-encoding.</summary>
    async Task<(bool ok, string error)> Mux(List<string> segments, List<double> starts, string tmp)
    {
        var list = System.IO.Path.Combine(tmp, "video.txt");
        var lines = new List<string>();
        for (int i = 0; i < segments.Count; i++)
        {
            lines.Add($"file '{segments[i].Replace('\\', '/').Replace("'", "'\\''")}'");
            if (i + 1 < segments.Count) lines.Add($"duration {Num(starts[i + 1] - starts[i])}");
        }
        await File.WriteAllLinesAsync(list, lines);

        var args = new StringBuilder("-hide_banner -loglevel error -y ");
        args.Append($"-itsoffset {Num(starts[0])} -f concat -safe 0 -i \"{list}\" ");
        bool audio = _mixer != null && File.Exists(_mixer.OutputPath) && new FileInfo(_mixer.OutputPath).Length > 0;
        args.Append(audio ? $"-i \"{_mixer!.OutputPath}\" -map 0:v -map 1:a " : "-map 0:v ");
        args.Append("-c copy ");
        if (_o.Codec == Codec.Hevc) args.Append("-tag:v hvc1 ");
        args.Append($"-movflags +faststart \"{_o.OutputPath}\"");

        var err = new StringBuilder();
        int code = await Ffmpeg.Run(args.ToString(), 600_000, err);
        return (code == 0 && File.Exists(_o.OutputPath), err.ToString().Trim());
    }

    static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A segment's first video timestamp (seconds from the recording's time zero), or null if the file
    /// holds no readable video.</summary>
    static async Task<double?> StartTime(string file)
    {
        var probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Ffmpeg.Path!)!, "ffprobe.exe");
        if (!File.Exists(probe)) probe = "ffprobe";
        var psi = new ProcessStartInfo(probe, $"-v error -select_streams v:0 -show_entries stream=start_time -of csv=p=0 \"{file}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        try
        {
            using var p = Process.Start(psi)!;
            var text = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? Math.Max(0, t) : null;
        }
        catch { return null; }
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    static void TryDeleteDir(string path) { try { Directory.Delete(path, true); } catch { } }
}
