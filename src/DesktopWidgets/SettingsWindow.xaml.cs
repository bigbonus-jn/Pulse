using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pulse;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    public event Action? SettingsChanged;

    private static readonly (string Key, string Display, bool Required)[] AllMonitors = new[]
    {
        ("CPU", "CPU", true),
        ("RAM", "RAM", false),
        ("GPU", "GPU", false),
        ("NET", "NET", false),
        ("DELAY", "DELAY", false),
        ("WIFI", "WIFI", false),
        ("IPADDR", "IP ADDR", false),
        ("IPDNS", "IP DNS", false),
        ("PROC", "PROC", false),
        ("TEMP", "TEMP", false),
    };

    public SettingsWindow(ConfigService config)
    {
        InitializeComponent();
        _config = config;
        BuildMonitorCheckboxes();
    }

    private void BuildMonitorCheckboxes()
    {
        foreach (var (key, display, required) in AllMonitors)
        {
            bool isChecked = _config.Data.MonitorVisibility.TryGetValue(key, out var vis) ? vis : true;

            var checkbox = new System.Windows.Controls.CheckBox
            {
                Content = required ? $"{display}  [LOCKED]" : display,
                IsChecked = isChecked,
                IsEnabled = !required,
                Tag = key,
            };

            if (required)
                checkbox.ToolTip = "基础监控项，不可关闭";

            checkbox.Checked += (_, _) => OnMonitorChecked(key, true);
            checkbox.Unchecked += (_, _) => OnMonitorChecked(key, false);

            MonitorCheckList.Children.Add(checkbox);
        }
    }

    private void OnMonitorChecked(string key, bool visible)
    {
        _config.Data.MonitorVisibility[key] = visible;
        _config.Save();
        SettingsChanged?.Invoke();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
