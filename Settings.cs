using System.Text.Json;

namespace CalendarFlyout;

/// <summary>Small per-user preferences, saved as settings.json next to the sign-in token.</summary>
internal sealed class Settings
{
    /// <summary>Flyout height in DIPs, if you've resized it.</summary>
    public double? FlyoutHeight { get; set; }

    private static string FilePath => Path.Combine(App.DataDir, "settings.json");
    private string? _saved;

    public static Settings Load()
    {
        Settings settings;
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { settings = new(); }
        settings._saved = JsonSerializer.Serialize(settings);
        return settings;
    }

    /// <summary>Writes the file only if something changed since it was loaded or last saved.</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this);
        if (json == _saved) return;
        try { File.WriteAllText(FilePath, json); _saved = json; }
        catch { /* not worth interrupting you over */ }
    }
}
