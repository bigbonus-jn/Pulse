using System.IO;
using System.Text.Json;

namespace DesktopWidgets;

public class WidgetConfig
{
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 340;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public double Opacity { get; set; } = 1.0;  // 0.2 ~ 1.0
}

public class ConfigService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopWidgets", "config.json");

    public WidgetConfig Data { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Data = JsonSerializer.Deserialize<WidgetConfig>(json) ?? new WidgetConfig();
            }
        }
        catch
        {
            Data = new WidgetConfig();
        }
    }

    private bool _saveScheduled;
    public void Save()
    {
        // Debounce saves to avoid thrashing on drag
        if (_saveScheduled) return;
        _saveScheduled = true;
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
            _saveScheduled = false;
            timer?.Dispose();
        }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }
}
