using System.IO;
using System.Text.Json;

namespace ReelCap;

public sealed class Settings
{
    public string Monitor { get; set; } = "";
    public int Fps { get; set; } = 60;
    public int Height { get; set; }
    public int Seconds { get; set; }
    public Codec Codec { get; set; } = Codec.H264;
    public bool UseGpu { get; set; } = true;
    public Quality Quality { get; set; } = Quality.High;
    public bool Cursor { get; set; } = true;
    public bool HideWhileRecording { get; set; }
    public bool AppMode { get; set; }           // record one application window instead of a monitor
    public string AppExe { get; set; } = "";    // last picked app, re-selected when it's running
    public bool AppAudio { get; set; } = true;  // app mode: the app's own audio
    public bool OtherAudio { get; set; }        // app mode: every other app's audio
    public bool SystemAudio { get; set; } = true; // screen mode: everything that plays
    public bool Mic { get; set; }
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReelCap");

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReelCap", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
