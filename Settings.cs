using System;
using System.IO;
using System.Text.Json;

namespace NovelGrabber;

/// <summary>Tiny persisted app settings (LocalAppData\NovelGrabber\settings.json).</summary>
public sealed class AppSettings
{
    public string GoogleTtsKey { get; set; } = "";
    public string LibraryView { get; set; } = "grid";   // "grid" | "list"
    public string Theme { get; set; } = "dark";         // "dark" | "darksepia" | "sepia" | "light"
    public bool AutoSortOnAdd { get; set; } = false;    // auto-file new books into series on import

    private static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovelGrabber", "settings.json");

    private static AppSettings? _cur;

    public static AppSettings Load()
    {
        if (_cur != null) return _cur;
        try
        {
            if (File.Exists(PathFile))
                _cur = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile));
        }
        catch { }
        return _cur ??= new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
