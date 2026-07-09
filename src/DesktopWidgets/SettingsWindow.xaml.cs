using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pulse;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    private readonly MainWindow _owner;
    public event Action? SettingsChanged;

    /// <summary>Locked = follow main window (default). Unlocked = free drag.</summary>
    private bool _isFollowing = true;

    private double _offsetX;  // horizontal offset from main window
    private double _offsetY;  // vertical offset from main window

    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private System.Windows.Controls.TextBox? _pingTargetBox;

    private static readonly (string Key, string Display, bool Required)[] AllMonitors = new[]
    {
        ("CPU", "CPU", true),
        ("RAM", "RAM", false),
        ("GPU", "GPU", false),
        ("NET", "NET", false),
        ("DELAY", "DELAY", false),
        ("RSSI", "RSSI", false),
        ("BAT", "BAT", false),
        ("IPADDR", "IP ADDR", false),
        ("IPDNS", "IP DNS", false),
        ("PROC", "PROC", false),
        ("TEMP", "TEMP", false),
    };

    public SettingsWindow(ConfigService config, MainWindow owner)
    {
        InitializeComponent();
        _config = config;
        _owner = owner;
        BuildMonitorCheckboxes();
        UpdateFollowButtonVisual();

        // Listen to main window location changes
        _owner.LocationChanged += Owner_LocationChanged;

        SourceInitialized += SettingsWindow_SourceInitialized;
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        if (_owner.Topmost)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SyncBackgroundAlpha(double alpha)
    {
        if (SettingsRootBorder == null) return;
        byte a = (byte)Math.Round(alpha * 255);
        SettingsRootBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, 0x0A, 0x0A, 0x14));
    }

    public void SyncTopmost(bool pinned)
    {
        Topmost = pinned;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (pinned)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Owner_LocationChanged(object? sender, EventArgs e)
    {
        if (_isFollowing)
            PositionNextToOwner();
    }

    public void PositionNextToOwner()
    {
        _offsetX = Left - _owner.Left;
        _offsetY = Top - _owner.Top;

        double settingsWidth = 280;
        double targetX, targetY;

        // Check right side first
        if (_owner.Left + _owner.Width + 8 + settingsWidth <= SystemParameters.PrimaryScreenWidth)
        {
            targetX = _owner.Left + _owner.Width + 8;
        }
        else
        {
            targetX = _owner.Left - settingsWidth - 8;
        }
        targetY = _owner.Top;

        Left = targetX;
        Top = targetY;

        // Store offsets for future follow updates
        _offsetX = Left - _owner.Left;
        _offsetY = Top - _owner.Top;
    }

    public void SnapToOwner()
    {
        // Called by MainWindow when settings opens — initial positioning
        PositionNextToOwner();
    }

    public void FollowOwner()
    {
        // Called when re-locking — snap back and start following
        PositionNextToOwner();
    }

    private void BuildMonitorCheckboxes()
    {
        // Define pairing order for double-column layout (semantic grouping)
        // Each pair sits on one row. DELAY gets its own full-width row.
        var pairs = new (string key, string display, bool required)[,]
        {
            { ("CPU", "CPU", true),   ("RAM", "RAM", false) },    // 核心硬件
            { ("GPU", "GPU", false),  ("NET", "NET", false) },    // 吞吐型
            { ("RSSI", "RSSI", false),("BAT", "BAT", false) },    // 状态型
            { ("IPADDR", "IP ADDR", false), ("IPDNS", "IP DNS", false) }, // 地址型
            { ("PROC", "PROC", false),("TEMP", "TEMP", false) },  // 系统型
        };

        for (int r = 0; r < pairs.GetLength(0); r++)
        {
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = CreateMonitorCheckbox(pairs[r, 0]);
            System.Windows.Controls.Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var right = CreateMonitorCheckbox(pairs[r, 1]);
            System.Windows.Controls.Grid.SetColumn(right, 1);
            row.Children.Add(right);

            MonitorCheckList.Children.Add(row);
        }

        // DELAY: full-width row with IP input (tightly coupled after checkbox)
        bool delayChecked = _config.Data.MonitorVisibility.TryGetValue("DELAY", out var dv) ? dv : true;
        var delayRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 4),
        };

        var delayCb = new System.Windows.Controls.CheckBox
        {
            Content = "DELAY",
            IsChecked = delayChecked,
            Tag = "DELAY",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        delayCb.Checked += (_, _) => OnMonitorChecked("DELAY", true);
        delayCb.Unchecked += (_, _) => OnMonitorChecked("DELAY", false);

        var ipLabel = new System.Windows.Controls.TextBlock
        {
            Text = "IP:",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xFF, 0xF9)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };

        _pingTargetBox = new System.Windows.Controls.TextBox
        {
            Text = _config.Data.PingTarget,
            Width = 100,
            FontSize = 10,
            ToolTip = "PING 目标地址",
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _pingTargetBox.LostFocus += PingTargetBox_LostFocus;
        _pingTargetBox.KeyDown += PingTargetBox_KeyDown;

        delayRow.Children.Add(delayCb);
        delayRow.Children.Add(ipLabel);
        delayRow.Children.Add(_pingTargetBox);

        MonitorCheckList.Children.Add(delayRow);
    }

    private System.Windows.Controls.CheckBox CreateMonitorCheckbox((string key, string display, bool required) item)
    {
        bool isChecked = _config.Data.MonitorVisibility.TryGetValue(item.key, out var vis) ? vis : true;
        var checkbox = new System.Windows.Controls.CheckBox
        {
            Content = item.required ? $"{item.display}  [LOCKED]" : item.display,
            IsChecked = isChecked,
            IsEnabled = !item.required,
            Tag = item.key,
        };

        if (item.required)
            checkbox.ToolTip = "基础监控项，不可关闭";

        checkbox.Checked += (_, _) => OnMonitorChecked(item.key, true);
        checkbox.Unchecked += (_, _) => OnMonitorChecked(item.key, false);

        return checkbox;
    }

    private void PingTargetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyPingTarget();
    }

    private void PingTargetBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPingTarget();
            // Move focus away from textbox
            (sender as System.Windows.Controls.TextBox)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private void ApplyPingTarget()
    {
        if (_pingTargetBox == null) return;
        string input = _pingTargetBox.Text.Trim();

        // Validate: accept valid IP or hostname
        if (string.IsNullOrWhiteSpace(input))
        {
            _pingTargetBox.Text = _config.Data.PingTarget;  // revert
            return;
        }

        // Quick IP format check
        if (IPAddress.TryParse(input, out _))
        {
            _config.Data.PingTarget = input;
            _config.Save();
            SettingsChanged?.Invoke();
        }
        else
        {
            // Could be a hostname — accept it, ping will validate at runtime
            _config.Data.PingTarget = input;
            _config.Save();
            SettingsChanged?.Invoke();
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

    private void FollowToggle_Click(object sender, RoutedEventArgs e)
    {
        _isFollowing = !_isFollowing;
        UpdateFollowButtonVisual();

        if (_isFollowing)
            FollowOwner();
    }

    private void UpdateFollowButtonVisual()
    {
        // L = locked/following, l = unlocked/free
        FollowToggle.Content = _isFollowing ? "L" : "l";
        FollowToggle.Foreground = _isFollowing
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
        FollowToggle.ToolTip = _isFollowing
            ? "锁定跟随主窗口（点击解锁自由拖动）"
            : "已解锁自由拖动（点击恢复跟随主窗口）";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _owner.LocationChanged -= Owner_LocationChanged;
        base.OnClosed(e);
    }
}
