using Microsoft.Win32;

namespace Pulse;

/// <summary>
/// Manages auto-start via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Pulse";

    private static readonly string ExePath = Environment.ProcessPath ?? "";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                var value = key?.GetValue(AppName) as string;
                return string.Equals(value, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.SetValue(AppName, ExePath);
        }
        catch { }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }
}
