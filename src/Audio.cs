using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReelCap;

/// <summary>Every audio input is captured as 48 kHz stereo float and mixed into one track.</summary>
public static class AudioFormat
{
    public const int Rate = 48000, Channels = 2;
    public static readonly WaveFormat Wave = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

    /// <summary>QPC now, in the 100 ns units WASAPI stamps packets with.</summary>
    public static long Now100ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}

/// <summary>One WASAPI capture (an app, every other app, all system audio, or the microphone). Packets are written
/// into a ring at the position their QPC timestamp gives on the recording's timeline, so inputs line up with each
/// other and with the video. Loopback sends nothing while silent; those gaps stay zero. Small clock drift between
/// the device and QPC is absorbed one sample at a time instead of with audible jumps. No allocation per packet.</summary>
public sealed class AudioInput : IDisposable
{
    const int RingFrames = AudioFormat.Rate * 8;
    const int Jump = AudioFormat.Rate / 20;    // 50 ms off: a real gap or discontinuity, re-place the packet
    const int Nudge = AudioFormat.Rate / 500;  // 2 ms off: drift, correct by one frame

    readonly WasapiRecorder _rec;
    readonly CaptureDataAvailableHandler _handler;
    readonly float[] _ring = new float[RingFrames * 2];
    readonly object _lock = new();
    long _base100ns = long.MinValue;
    long _cursor = -1;   // next frame to write
    long _readPos;       // frames before this have been mixed
    float _lastL, _lastR;

    public string Name { get; }

    AudioInput(string name, WasapiRecorder rec)
    {
        Name = name;
        _rec = rec;
        _handler = (buffer, flags, _, qpc) => OnPacket(buffer, flags, (long)qpc);
        _rec.DataAvailable += _handler;
    }

    public static AudioInput System() =>
        Start("System audio", new WasapiRecorderBuilder().WithLoopbackCapture().WithFormat(AudioFormat.Wave).Build());

    public static AudioInput Microphone() =>
        Start("Microphone", new WasapiRecorderBuilder().WithDefaultDeviceStreamRouting().WithFormat(AudioFormat.Wave).Build());

    /// <summary>Only what this process tree plays, or (exclude) everything except it. Windows 10 2004 or later.</summary>
    public static async Task<AudioInput> Process(int pid, bool exclude) =>
        Start(exclude ? "Other apps" : "App audio", await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)pid, exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(AudioFormat.Wave).BuildAsync());

    static AudioInput Start(string name, WasapiRecorder rec)
    {
        var input = new AudioInput(name, rec);
        rec.StartRecording();
        return input;
    }

    /// <summary>Sets the recording's time zero; packets before it are ignored.</summary>
    public void Begin(long base100ns)
    {
        lock (_lock) { _base100ns = base100ns; _cursor = -1; _readPos = 0; Array.Clear(_ring); }
    }

    long FrameAt(long t100ns) => (t100ns - _base100ns) * AudioFormat.Rate / 10_000_000;

    void OnPacket(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpc)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        int frames = samples.Length / 2;
        if (frames == 0) return;
        bool silent = flags.HasFlag(AudioClientBufferFlags.Silent);
        lock (_lock)
        {
            if (_base100ns == long.MinValue) return;
            long at = flags.HasFlag(AudioClientBufferFlags.TimestampError) && _cursor >= 0 ? _cursor : FrameAt(qpc);
            int skip = 0;
            if (_cursor < 0 || flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity) || Math.Abs(at - _cursor) > Jump)
                _cursor = at;
            else if (at - _cursor > Nudge)
            {
                Put(_cursor++, _lastL, _lastR);   // device running fast against QPC: stretch by one frame
            }
            else if (_cursor - at > Nudge) skip = 1; // running slow: drop one frame

            for (int i = skip; i < frames; i++, _cursor++)
            {
                float l = silent ? 0 : samples[i * 2], r = silent ? 0 : samples[i * 2 + 1];
                Put(_cursor, l, r);
                _lastL = l; _lastR = r;
            }
        }
    }

    void Put(long pos, float l, float r)
    {
        if (pos < _readPos || pos >= _readPos + RingFrames) return; // too late, or the mixer stalled
        int i = (int)(pos % RingFrames) * 2;
        _ring[i] = l;
        _ring[i + 1] = r;
    }

    /// <summary>Adds frames [from, from + count) into dst and clears them from the ring.</summary>
    public void MixInto(long from, int count, float[] dst)
    {
        lock (_lock)
        {
            for (int f = 0; f < count; f++)
            {
                int i = (int)((from + f) % RingFrames) * 2;
                dst[f * 2] += _ring[i];
                dst[f * 2 + 1] += _ring[i + 1];
                _ring[i] = _ring[i + 1] = 0;
            }
            _readPos = from + count;
        }
    }

    public void Dispose()
    {
        _rec.DataAvailable -= _handler;
        try { _rec.StopRecording(); } catch { }
        _rec.Dispose();
    }
}

/// <summary>Mixes the inputs into one stereo track, 250 ms behind real time so late packets still land, and streams
/// it to a long-lived ffmpeg that limits and encodes it to AAC for the whole recording.</summary>
public sealed class AudioMixer
{
    const int Lag = AudioFormat.Rate / 4;
    const int Chunk = AudioFormat.Rate / 50;   // 20 ms

    readonly List<AudioInput> _inputs;
    readonly float[] _mix = new float[Chunk * 2];
    readonly byte[] _bytes = new byte[Chunk * 2 * 4];
    Process? _ffmpeg;
    Thread? _thread;
    long _base100ns, _pos;
    long _end = long.MaxValue;   // frame to stop at
    readonly ManualResetEventSlim _done = new();

    public string OutputPath { get; }

    public AudioMixer(List<AudioInput> inputs, string outputPath)
    {
        _inputs = inputs;
        OutputPath = outputPath;
    }

    public void Start(long base100ns)
    {
        _base100ns = base100ns;
        foreach (var i in _inputs) i.Begin(base100ns);
        var psi = new ProcessStartInfo(Ffmpeg.Path!,
            "-hide_banner -loglevel error -y -f f32le -ar 48000 -ac 2 -i pipe:0 " +
            $"-af alimiter=limit=0.97:level=0 -c:a aac -b:a 192k \"{OutputPath}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
        _ffmpeg = global::System.Diagnostics.Process.Start(psi)!;
        _ = _ffmpeg.StandardError.ReadToEndAsync();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ReelCap mixer" };
        _thread.Start();
    }

    long NowFrame() => (AudioFormat.Now100ns() - _base100ns) * AudioFormat.Rate / 10_000_000;

    void Loop()
    {
        var stdin = _ffmpeg!.StandardInput.BaseStream;
        try
        {
            while (true)
            {
                long target = Math.Min(NowFrame() - Lag, Interlocked.Read(ref _end));
                while (_pos < target)
                {
                    int n = (int)Math.Min(Chunk, target - _pos);
                    Array.Clear(_mix, 0, n * 2);
                    foreach (var i in _inputs) i.MixInto(_pos, n, _mix);
                    Buffer.BlockCopy(_mix, 0, _bytes, 0, n * 8);
                    stdin.Write(_bytes, 0, n * 8);
                    _pos += n;
                }
                if (_pos >= Interlocked.Read(ref _end)) break;
                Thread.Sleep(10);
            }
        }
        catch { /* ffmpeg went away */ }
        try { stdin.Close(); } catch { }
        _done.Set();
    }

    /// <summary>Ends the track at <paramref name="stop100ns"/> (after the late packets for it have arrived) and
    /// waits for the encoder to finish the file.</summary>
    public async Task StopAsync(long stop100ns)
    {
        Interlocked.Exchange(ref _end, Math.Max(0, (stop100ns - _base100ns) * AudioFormat.Rate / 10_000_000));
        await Task.Run(() => _done.Wait(5000));
        if (_ffmpeg != null)
        {
            var exit = _ffmpeg.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(30000)) != exit) { try { _ffmpeg.Kill(); } catch { } }
        }
        foreach (var i in _inputs) i.Dispose();
        if (Environment.GetEnvironmentVariable("REELCAP_KEEP") != null)
            File.AppendAllText(OutputPath + ".log", $"end={_end} pos={_pos} now={NowFrame()} loopDone={_done.IsSet} exit={_ffmpeg?.ExitCode}\n");
    }
}
