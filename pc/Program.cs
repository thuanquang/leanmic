namespace LeanMic;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\LeanMic.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("LeanMic is already running. Look for its icon in the system tray.", "LeanMic");
            return;
        }
        ApplicationConfiguration.Initialize();
        Theme.Watch();
        Application.Run(new MainForm());
    }
}

static class Log
{
    static readonly object Gate = new();
    static readonly string Path = System.IO.Path.Combine(Settings.Dir, "leanmic.log");

    static Log()
    {
        try { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(Path, ""); } catch { }
    }

    public static void Write(string msg)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); } catch { }
        }
    }
}

sealed class Settings
{
    public static readonly string Dir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanMic");
    static string FilePath => System.IO.Path.Combine(Dir, "settings.json");

    public string? DeviceId { get; set; }
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public bool NoiseSuppression { get; set; }

    public static Settings Load()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
