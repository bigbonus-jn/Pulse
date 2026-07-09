using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Management;
using LibreHardwareMonitor.Hardware;

namespace Pulse;

public partial class MainWindow : Window
{
    private NotifyIconWrapper? _notifyIcon;
    private readonly DispatcherTimer _dataTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PerformanceCounter _cpuCounter = new("Processor", "% Processor Time", "_Total");
    private readonly PerformanceCounter _memCounter = new("Memory", "Available MBytes");
    private readonly ConfigService _config = new();
    private SettingsWindow? _settingsWindow;
    private bool _cpuFirstSample = true;
     private bool _isCompact;
     public bool IsCompact => _isCompact;
     private bool _isPinned = true;  // topmost pin state
    private bool _isLocked;        // lock position (prevent drag)
    private bool _isDraggingOpacity;  // opacity bar drag state
    private double _bgAlpha = 1.0;  // background alpha 0.2~1.0
    private System.Windows.Threading.DispatcherTimer? _resizeTimer;  // debounced resize
    private int _slowTickCounter;  // counts 1s ticks for 5s slow metrics
    private int _ipTickCounter;    // counts 1s ticks for 30s IP/DNS refresh
    private int _wifiTickCounter;  // counts 1s ticks for WiFi refresh (WMI=5s, netsh=30s)

    // Battery
    private bool _hasBattery;  // true if system has a battery (laptop)

    // WiFi signal: WMI vs netsh
    private bool _wifiWmiAvailable;  // true if WMI MSNdis_80211 works
    private bool _wifiWmiTested;     // true if we've tested WMI at least once

    // Network monitoring
    private PerformanceCounter? _netSentCounter;
    private PerformanceCounter? _netReceivedCounter;
    private bool _netCountersInitialized;
    private string _netAdapterName = "";
    private int _netRescanCooldown;

    // GPU monitoring
    private string _gpuName = "N/A";
    private double _gpuVramMB;
    private List<PerformanceCounter> _gpuCounters = new();
    private bool _gpuCountersInitialized;

    // Ping monitoring
    private readonly System.Net.NetworkInformation.Ping _pingSender = new();

    // Pre-allocated colors & brushes (avoid GC per-tick)
    private static readonly SolidColorBrush CyanText = new(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9));
    private static readonly SolidColorBrush MagentaText = new(System.Windows.Media.Color.FromRgb(0xFF, 0x2A, 0x6D));
    private static readonly SolidColorBrush YellowText = new(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush WhiteText = new(System.Windows.Media.Color.FromRgb(0xE6, 0xFF, 0xFF));
    private static readonly SolidColorBrush PurpleText = new(System.Windows.Media.Color.FromRgb(0xBD, 0x93, 0xF9));
    private static readonly SolidColorBrush RedText = new(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush TealText = new(System.Windows.Media.Color.FromRgb(0x00, 0xCE, 0xD1));
    private static readonly SolidColorBrush DimText = new(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));  // N/A / hardware-limited

    // LibreHardwareMonitor for CPU/GPU temperature
    private readonly Computer _computer;

    // Win32 API
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    public MainWindow()
    {
        InitializeComponent();
        _config.Load();
        Left = _config.Data.X;
        Top = _config.Data.Y;
        Width = _config.Data.Width;
        Height = _config.Data.Height;

        // Restore topmost state
        _isPinned = _config.Data.Topmost;
        Topmost = _isPinned;

        // Restore lock state
        _isLocked = _config.Data.Locked;

        // Restore opacity (background alpha only, not window opacity)
        var bgAlpha = Math.Clamp(_config.Data.Opacity, 0.2, 1.0);
        _bgAlpha = bgAlpha;
        ApplyBackgroundAlpha(bgAlpha);

        // Initialize LibreHardwareMonitor for CPU/GPU temperature
        _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true };
        try { _computer.Open(); } catch { }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _notifyIcon = new NotifyIconWrapper(this);
        StartScanlineAnimation();
        _dataTimer.Tick += PushSystemData;
        _dataTimer.Start();
        InitializeNetworkCounters();
        InitializeGpuCounters();
        DetectBattery();
        UpdatePinButtonVisual();
        UpdateLockButtonVisual();
        UpdateOpacityBarVisual();
        ApplyMonitorVisibility();
        UpdatePingLabel();

        // Sync auto-start state from config to registry
        var autoStart = new AutoStartService();
        if (_config.Data.AutoStart && !autoStart.IsEnabled) autoStart.Enable();
        if (!_config.Data.AutoStart && autoStart.IsEnabled) autoStart.Disable();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        if (_isPinned)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        UpdatePinButtonVisual();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _config.Data.X = Left;
        _config.Data.Y = Top;
        _config.Save();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _config.Data.Width = Width;
        _config.Data.Height = Height;
        _config.Save();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settingsWindow?.Close();
        _config.Save();
        _dataTimer.Stop();
        _notifyIcon?.Dispose();
        try { _computer.Close(); } catch { }
    }

    // ── Drag ──
    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isLocked) DragMove();
    }

    // ── Settings entry (3 dots click — toggle open/close) ──
    private void Dots_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;  // prevent drag
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
            _settingsWindow.Close();
        else
            OpenSettings();
    }

    private void OpenSettings()
    {
        _settingsWindow = new SettingsWindow(_config, this);
        _settingsWindow.SettingsChanged += ApplyMonitorVisibility;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;

        _settingsWindow.Show();
        _settingsWindow.SnapToOwner();
        _settingsWindow.SyncBackgroundAlpha(_bgAlpha);
        _settingsWindow.SyncTopmost(_isPinned);
    }

    // ── Monitor visibility ──
    private void ApplyMonitorVisibility()
    {
        var vis = _config.Data.MonitorVisibility;

        CpuSection.Visibility   = vis.GetValueOrDefault("CPU", true)    ? Visibility.Visible : Visibility.Collapsed;
        RamSection.Visibility   = vis.GetValueOrDefault("RAM", true)    ? Visibility.Visible : Visibility.Collapsed;
        GpuSection.Visibility   = vis.GetValueOrDefault("GPU", true)    ? Visibility.Visible : Visibility.Collapsed;
        NetSection.Visibility   = vis.GetValueOrDefault("NET", true)    ? Visibility.Visible : Visibility.Collapsed;
        PingSection.Visibility  = vis.GetValueOrDefault("DELAY", true)  ? Visibility.Visible : Visibility.Collapsed;
        WifiSection.Visibility  = vis.GetValueOrDefault("WIFI", true)   ? Visibility.Visible : Visibility.Collapsed;
        ProcSection.Visibility  = vis.GetValueOrDefault("PROC", true)   ? Visibility.Visible : Visibility.Collapsed;
        TempSection.Visibility  = vis.GetValueOrDefault("TEMP", true)   ? Visibility.Visible : Visibility.Collapsed;
        BatSection.Visibility   = (_hasBattery && vis.GetValueOrDefault("BAT", true)) ? Visibility.Visible : Visibility.Collapsed;

        // IP row in bottom bar: show if either IPADDR or IPDNS is enabled
        bool showIp = vis.GetValueOrDefault("IPADDR", true) || vis.GetValueOrDefault("IPDNS", true);
        IpRow.Visibility = showIp ? Visibility.Visible : Visibility.Collapsed;
        // Hide individual parts if user toggled them off
        AddrLabel.Visibility = AddrValue.Visibility = vis.GetValueOrDefault("IPADDR", true) ? Visibility.Visible : Visibility.Collapsed;
        DnsLabel.Visibility = DnsValue.Visibility = vis.GetValueOrDefault("IPDNS", true) ? Visibility.Visible : Visibility.Collapsed;

        // Update DELAY label to reflect current PingTarget
        UpdatePingLabel();

        // Resize window to fit visible content
        ScheduleResize();
    }

    private void UpdatePingLabel()
    {
        PingLabel.Text = _isCompact ? "DELAY" : $"DELAY({_config.Data.PingTarget})";
    }

    // ── Debounced window resize ──
    private void ScheduleResize()
    {
        _resizeTimer?.Stop();
        _resizeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
            IsEnabled = false
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer!.Stop();
            ExecuteResize();
        };
        _resizeTimer.Start();
    }

    private void ExecuteResize()
    {
        // Let WPF auto-size to content height
        SizeToContent = System.Windows.SizeToContent.Height;
        // Force synchronous layout pass
        UpdateLayout();
        // Capture the auto-sized height
        var finalHeight = ActualHeight;
        // Switch back to manual and lock the height
        SizeToContent = System.Windows.SizeToContent.Manual;
        Height = finalHeight;
    }

    // ── Compact mode ──
    private void CompactToggle_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = !_isCompact;
        ApplyCompactMode();
    }

    public void ToggleCompactMode()
    {
        _isCompact = !_isCompact;
        ApplyCompactMode();
    }

    private void ApplyCompactMode()
    {
        CompactToggle.Content = _isCompact ? "\u25AB" : "\u25A3";

        // Toggle value visibility
        CpuValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        RamValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        GpuValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        NetValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        PingValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        WifiValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        // ADDR & DNS: always show values (text-only, no bar)
        TempValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        ProcValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        BatValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;

        // NET tags
        NetUpTag.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        NetDnTag.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;

        // TEMP tags
        TempCpuTag.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        TempGpuTag.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (_isCompact)
        {
            // Compact: switch each Grid to 2-column layout [label | bar]
            SetMetricGridCompact(CpuSection, CpuHeaderRow, CpuBarContainer, CpuLabel);
            SetMetricGridCompact(RamSection, RamHeaderRow, RamBarContainer, RamLabel);
            SetMetricGridCompact(GpuSection, GpuHeaderRow, GpuBarContainer, GpuLabel);
            SetMetricGridCompact(PingSection, PingHeaderRow, PingBarContainer, PingLabel);
            PingLabel.Text = "DELAY";
            SetMetricGridCompact(WifiSection, WifiHeaderRow, WifiBarContainer, WifiLabel);
            SetMetricGridCompact(BatSection, BatHeaderRow, BatBarContainer, BatLabel);
            SetMetricGridCompact(ProcSection, ProcHeaderRow, ProcBarContainer, ProcLabel);

            // NET: label left, bars right
            NetSection.ColumnDefinitions.Clear();
            NetSection.RowDefinitions.Clear();
            NetSection.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(32) });
            NetSection.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Grid.SetColumn(NetHeaderRow, 0); System.Windows.Controls.Grid.SetRow(NetHeaderRow, 0);
            System.Windows.Controls.Grid.SetColumn(NetBarsGrid, 1); System.Windows.Controls.Grid.SetRow(NetBarsGrid, 0);
            NetBarsGrid.Margin = new Thickness(4, 1, 0, 0);
            NetUpBarContainer.Height = 5; NetDnBarContainer.Height = 5;
            NetLabel.FontSize = 9;

            // ADDR & DNS: now in bottom bar IP row — shrink in compact
            AddrLabel.FontSize = 9;
            DnsLabel.FontSize = 9;
            AddrValue.FontSize = 9;
            DnsValue.FontSize = 9;

            // TEMP: label left, bars right (same as NET)
            TempSection.ColumnDefinitions.Clear();
            TempSection.RowDefinitions.Clear();
            TempSection.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });
            TempSection.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Grid.SetColumn(TempHeaderRow, 0); System.Windows.Controls.Grid.SetRow(TempHeaderRow, 0);
            System.Windows.Controls.Grid.SetColumn(TempBarsGrid, 1); System.Windows.Controls.Grid.SetRow(TempBarsGrid, 0);
            TempBarsGrid.Margin = new Thickness(4, 1, 0, 0);
            TempCpuBarContainer.Height = 5; TempGpuBarContainer.Height = 5;
            TempLabel.FontSize = 9;

            CpuSection.Margin = new Thickness(0, 0, 0, 2);
            RamSection.Margin = new Thickness(0, 0, 0, 2);
            GpuSection.Margin = new Thickness(0, 0, 0, 2);
            NetSection.Margin = new Thickness(0, 0, 0, 2);
            PingSection.Margin = new Thickness(0, 0, 0, 2);
            WifiSection.Margin = new Thickness(0, 0, 0, 2);
            BatSection.Margin = new Thickness(0, 0, 0, 2);
            ProcSection.Margin = new Thickness(0, 0, 0, 2);
            TempSection.Margin = new Thickness(0, 0, 0, 0);
        }
        else
        {
            // Normal: restore 2-row layout
            SetMetricGridNormal(CpuSection, CpuHeaderRow, CpuBarContainer, CpuLabel);
            SetMetricGridNormal(RamSection, RamHeaderRow, RamBarContainer, RamLabel);
            SetMetricGridNormal(GpuSection, GpuHeaderRow, GpuBarContainer, GpuLabel);
            SetMetricGridNormal(PingSection, PingHeaderRow, PingBarContainer, PingLabel);
            PingLabel.Text = $"DELAY({_config.Data.PingTarget})";
            SetMetricGridNormal(WifiSection, WifiHeaderRow, WifiBarContainer, WifiLabel);
            SetMetricGridNormal(BatSection, BatHeaderRow, BatBarContainer, BatLabel);
            SetMetricGridNormal(ProcSection, ProcHeaderRow, ProcBarContainer, ProcLabel);

            // NET: restore
            NetSection.ColumnDefinitions.Clear();
            NetSection.RowDefinitions.Clear();
            NetSection.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            NetSection.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(NetHeaderRow, 0); System.Windows.Controls.Grid.SetRow(NetHeaderRow, 0);
            System.Windows.Controls.Grid.SetColumn(NetBarsGrid, 0); System.Windows.Controls.Grid.SetRow(NetBarsGrid, 1);
            NetBarsGrid.Margin = new Thickness(0, 3, 0, 0);
            NetUpBarContainer.Height = 6; NetDnBarContainer.Height = 6;
            NetLabel.FontSize = 11;

            // ADDR & DNS: restore font in bottom bar
            AddrLabel.FontSize = 11;
            DnsLabel.FontSize = 11;
            AddrValue.FontSize = 11;
            DnsValue.FontSize = 11;

            // TEMP: restore
            TempSection.ColumnDefinitions.Clear();
            TempSection.RowDefinitions.Clear();
            TempSection.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            TempSection.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(TempHeaderRow, 0); System.Windows.Controls.Grid.SetRow(TempHeaderRow, 0);
            System.Windows.Controls.Grid.SetColumn(TempBarsGrid, 0); System.Windows.Controls.Grid.SetRow(TempBarsGrid, 1);
            TempBarsGrid.Margin = new Thickness(0, 3, 0, 0);
            TempCpuBarContainer.Height = 6; TempGpuBarContainer.Height = 6;
            TempLabel.FontSize = 11;

            CpuSection.Margin = new Thickness(0, 0, 0, 7);
            RamSection.Margin = new Thickness(0, 0, 0, 7);
            GpuSection.Margin = new Thickness(0, 0, 0, 7);
            NetSection.Margin = new Thickness(0, 0, 0, 7);
            PingSection.Margin = new Thickness(0, 0, 0, 7);
            WifiSection.Margin = new Thickness(0, 0, 0, 7);
            BatSection.Margin = new Thickness(0, 0, 0, 7);
            ProcSection.Margin = new Thickness(0, 0, 0, 7);
            TempSection.Margin = new Thickness(0, 0, 0, 0);
        }

        // Resize window to fit content after compact/normal switch
        ScheduleResize();

        // Sync tray menu check state
        _notifyIcon?.SyncCompactState(_isCompact);
    }

    private void SetMetricGridCompact(System.Windows.Controls.Grid section, System.Windows.Controls.DockPanel header, System.Windows.Controls.Grid bar, System.Windows.Controls.TextBlock label)
    {
        section.ColumnDefinitions.Clear();
        section.RowDefinitions.Clear();
        section.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });
        section.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        System.Windows.Controls.Grid.SetColumn(header, 0); System.Windows.Controls.Grid.SetRow(header, 0);
        System.Windows.Controls.Grid.SetColumn(bar, 1); System.Windows.Controls.Grid.SetRow(bar, 0);

        header.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(2, 0, 0, 0);
        bar.Height = 6;
        label.FontSize = 9;
    }

    private void SetMetricGridNormal(System.Windows.Controls.Grid section, System.Windows.Controls.DockPanel header, System.Windows.Controls.Grid bar, System.Windows.Controls.TextBlock label)
    {
        section.ColumnDefinitions.Clear();
        section.RowDefinitions.Clear();
        section.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        section.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        System.Windows.Controls.Grid.SetColumn(header, 0); System.Windows.Controls.Grid.SetRow(header, 0);
        System.Windows.Controls.Grid.SetColumn(bar, 0); System.Windows.Controls.Grid.SetRow(bar, 1);

        header.VerticalAlignment = VerticalAlignment.Top;
        bar.Margin = new Thickness(0, 3, 0, 0);
        bar.Height = 8;
        label.FontSize = 11;
    }

    // ── Scanline ──
    private void StartScanlineAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = -4,
            To = ActualHeight + 4,
            Duration = TimeSpan.FromSeconds(4),
            RepeatBehavior = RepeatBehavior.Forever
        };
        ScanlineTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // ── Exit ──
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Pin / Topmost toggle ──
    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        _config.Data.Topmost = _isPinned;
        _config.Save();
        UpdatePinButtonVisual();

        if (_isPinned)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        _settingsWindow?.SyncTopmost(_isPinned);
    }

    private void UpdatePinButtonVisual()
    {
        // T = pinned (topmost), t = unpinned
        PinToggle.Content = _isPinned ? "T" : "t";
        PinToggle.Foreground = _isPinned
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
    }

    // ── Lock position toggle ──
    private void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        _config.Data.Locked = _isLocked;
        _config.Save();
        UpdateLockButtonVisual();
    }

    private void UpdateLockButtonVisual()
    {
        // L = locked, l = unlocked
        LockToggle.Content = _isLocked ? "L" : "l";
        LockToggle.Foreground = _isLocked
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
    }

    // ── Opacity bar (cyberpunk custom control) ──
    private void OpacityBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingOpacity = true;
        UpdateOpacityFromMouse(e);
        ((System.Windows.IInputElement)sender).CaptureMouse();
    }

    private void OpacityBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDraggingOpacity) UpdateOpacityFromMouse(e);
    }

    private void OpacityBar_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingOpacity = false;
        ((System.Windows.IInputElement)sender).ReleaseMouseCapture();
    }

    private void UpdateOpacityFromMouse(System.Windows.Input.MouseEventArgs e)
    {
        if (OpacityBar == null) return;
        var pos = e.GetPosition(OpacityBar);
        double ratio = Math.Clamp(pos.X / OpacityBar.ActualWidth, 0, 1);
        _bgAlpha = 0.2 + ratio * 0.8;  // map 0~1 → 0.2~1.0
        ApplyBackgroundAlpha(_bgAlpha);
        UpdateOpacityBarVisual();
        _config.Data.Opacity = _bgAlpha;
        _config.Save();
    }

    private void ApplyBackgroundAlpha(double alpha)
    {
        if (RootBorder == null) return;
        byte a = (byte)Math.Round(alpha * 255);
        RootBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, 0x0A, 0x0A, 0x14));
        _settingsWindow?.SyncBackgroundAlpha(alpha);
    }

    private void UpdateOpacityBarVisual()
    {
        if (OpacityBar == null || OpacityFill == null || OpacityThumb == null) return;
        double ratio = (_bgAlpha - 0.2) / 0.8;  // 0~1
        double fillWidth = ratio * OpacityBar.ActualWidth;
        OpacityFill.Width = Math.Max(0, fillWidth);
        // Position thumb at the end of the fill
        OpacityThumb.SetValue(System.Windows.FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
        OpacityThumb.Margin = new Thickness(Math.Max(0, fillWidth - 3), 0, 0, 0);
    }

    // ── Click-through ──
    public void ToggleClickThrough()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_EX_TRANSPARENT) != 0)
            SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
        else
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
    }

    // ── Auto-start config bridge ──
    public void SetAutoStartConfig(bool enabled)
    {
        _config.Data.AutoStart = enabled;
        _config.Save();
    }

    public void ShowWindow()
    {
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ── Data push ──
    private void PushSystemData(object? sender, EventArgs e)
    {
        float cpuUsage = _cpuCounter.NextValue();
        if (_cpuFirstSample) { _cpuFirstSample = false; return; }
        float availableMem = _memCounter.NextValue();
        float totalMem = GetTotalPhysicalMemory();
        float memUsage = totalMem > 0 ? ((totalMem - availableMem) / totalMem) * 100f : 0f;

        // CPU
        double cpu = Math.Round(Math.Min(100, Math.Max(0, cpuUsage)), 1);
        CpuValue.Text = cpu.ToString("F1") + "%";
        CpuFill.Width = (cpu / 100.0) * CpuBarContainer.ActualWidth;
        if (cpu > 80)
        {
            CpuFill.Fill = FindResource("MagentaBar") as System.Windows.Media.Brush;
            CpuValue.Foreground = MagentaText;
        }
        else
        {
            CpuFill.Fill = FindResource("CyanBar") as System.Windows.Media.Brush;
            CpuValue.Foreground = CyanText;
        }

        // RAM
        double mem = Math.Round(Math.Min(100, Math.Max(0, memUsage)), 1);
        double usedGB = Math.Round((totalMem - availableMem) / 1024.0, 1);
        double totalGB = Math.Round(totalMem / 1024.0, 1);
        RamValue.Text = $"{usedGB}/{totalGB} GB";
        RamFill.Width = (mem / 100.0) * RamBarContainer.ActualWidth;
        if (mem > 85)
        {
            RamFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
            RamValue.Foreground = YellowText;
        }
        else
        {
            RamFill.Fill = FindResource("MagentaBar") as System.Windows.Media.Brush;
            RamValue.Foreground = MagentaText;
        }

        // GPU
        double gpu = 0;
        if (_gpuCountersInitialized)
        {
            try
            {
                float total = 0;
                foreach (var c in _gpuCounters)
                    total += c.NextValue();
                gpu = Math.Round(Math.Min(100, total), 1);
            }
            catch
            {
                foreach (var c in _gpuCounters)
                    try { c.Dispose(); } catch { }
                _gpuCounters.Clear();
                _gpuCountersInitialized = false;
                InitializeGpuCounters();
            }
        }
        GpuValue.Text = _gpuCountersInitialized ? gpu.ToString("F0") + "%" : "N/A";
        GpuFill.Width = (gpu / 100.0) * GpuBarContainer.ActualWidth;

        // Network
        double netUpKbps = 0, netDnKbps = 0;
        if (_netCountersInitialized && _netSentCounter != null && _netReceivedCounter != null)
        {
            try
            {
                float sentBytes = _netSentCounter.NextValue();
                float recvBytes = _netReceivedCounter.NextValue();
                netUpKbps = Math.Round(sentBytes / 1024.0, 1);
                netDnKbps = Math.Round(recvBytes / 1024.0, 1);
                if (sentBytes == 0 && recvBytes == 0)
                {
                    _netRescanCooldown++;
                    if (_netRescanCooldown >= 5) { _netRescanCooldown = 0; RescanNetworkAdapters(); }
                }
                else { _netRescanCooldown = 0; }
            }
            catch { RescanNetworkAdapters(); }
        }
        else if (!_netCountersInitialized)
        {
            _netRescanCooldown++;
            if (_netRescanCooldown >= 3) { _netRescanCooldown = 0; RescanNetworkAdapters(); }
        }
        NetValue.Text = $"{FmtSpeed(netUpKbps)} / {FmtSpeed(netDnKbps)}";
        double netMax = 10240;
        if (NetUpBarContainer.ActualWidth > 0)
            NetUpFill.Width = Math.Min(1, netUpKbps / netMax) * NetUpBarContainer.ActualWidth;
        if (NetDnBarContainer.ActualWidth > 0)
            NetDnFill.Width = Math.Min(1, netDnKbps / netMax) * NetDnBarContainer.ActualWidth;

        // Date & Time
        DateLabel.Text = DateTime.Now.ToString("yyyy-MM-dd");
        ClockLabel.Text = DateTime.Now.ToString("HH:mm:ss");

        // Position UptimeLabel to the right of the centered clock
        Dispatcher.BeginInvoke(() =>
        {
            if (ClockLabel.ActualWidth > 0)
            {
                var bar = (FrameworkElement)ClockLabel.Parent;
                double barCenter = bar.ActualWidth / 2;
                double clockRight = barCenter + ClockLabel.ActualWidth / 2;
                UptimeLabel.Margin = new Thickness(clockRight + 4, 0, 0, 0);
            }
        });

        // Uptime (every 1s — WMI query must be off-UI-thread)
        UpdateUptimeAsync();

        // DELAY / PING (every 1s — moved from slow metrics for responsiveness)
        UpdatePing();

        // Slow metrics (every 5s): TEMP, PROC, WIFI, BAT
        _slowTickCounter++;
        if (_slowTickCounter >= 5)
        {
            _slowTickCounter = 0;
            UpdateSlowMetricsAsync();
        }

        // IP / DNS (every 30s — rarely changes, low refresh is fine)
        _ipTickCounter++;
        if (_ipTickCounter >= 30)
        {
            _ipTickCounter = 0;
            UpdateIpDnsAsync();
        }
    }

    private bool _slowMetricsInProgress;

    private async void UpdateSlowMetricsAsync()
    {
        if (_slowMetricsInProgress) return;  // prevent overlapping
        _slowMetricsInProgress = true;

        try
        {
            // ── TEMP: LibreHardwareMonitor (can block 100-500ms) ──
            double cpuTemp = 0; bool cpuTempAvailable = false;
            bool cpuHasSensors = false;
            double gpuTemp = 0; bool gpuTempAvailable = false;
            bool gpuHasSensors = false;
            bool gpuHardwareFound = false;

            await Task.Run(() =>
            {
                try
                {
                    foreach (var hardware in _computer.Hardware)
                    {
                        hardware.Update();

                        if (hardware.HardwareType == HardwareType.Cpu && !cpuTempAvailable)
                        {
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor.SensorType != SensorType.Temperature) continue;
                                if (sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
                                cpuHasSensors = true;
                                if (sensor.Value.HasValue)
                                {
                                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                    { cpuTemp = Math.Round(sensor.Value.Value, 0); cpuTempAvailable = true; break; }
                                    if (!cpuTempAvailable)
                                    { cpuTemp = Math.Round(sensor.Value.Value, 0); cpuTempAvailable = true; }
                                }
                            }
                        }

                        if (hardware.HardwareType == HardwareType.GpuNvidia ||
                            hardware.HardwareType == HardwareType.GpuAmd ||
                            hardware.HardwareType == HardwareType.GpuIntel)
                        {
                            gpuHardwareFound = true;
                            if (!gpuTempAvailable)
                            {
                                foreach (var sensor in hardware.Sensors)
                                {
                                    if (sensor.SensorType != SensorType.Temperature) continue;
                                    if (sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
                                    gpuHasSensors = true;
                                    if (sensor.Value.HasValue)
                                    {
                                        if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                        { gpuTemp = Math.Round(sensor.Value.Value, 0); gpuTempAvailable = true; break; }
                                        if (!gpuTempAvailable)
                                        { gpuTemp = Math.Round(sensor.Value.Value, 0); gpuTempAvailable = true; }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });

            // Update TEMP UI (back on UI thread)
            string cpuTempStr = cpuTempAvailable ? $"{cpuTemp:F0}°" : "N/A";
            string gpuTempStr = gpuTempAvailable ? $"{gpuTemp:F0}°" : "N/A";
            TempValue.Text = $"{cpuTempStr} / {gpuTempStr}";

            TempCpuTag.Text = "CPU";
            if (cpuTempAvailable)
            {
                TempCpuTag.ToolTip = null;
                if (TempCpuBarContainer.ActualWidth > 0)
                {
                    TempCpuFill.Width = (cpuTemp / 100.0) * TempCpuBarContainer.ActualWidth;
                    TempCpuFill.Fill = cpuTemp > 80 ? FindResource("RedBar") as System.Windows.Media.Brush
                                     : cpuTemp > 60 ? FindResource("YellowBar") as System.Windows.Media.Brush
                                     : FindResource("CyanBar") as System.Windows.Media.Brush;
                }
                TempCpuTag.Foreground = FindResource("CyanBar") as System.Windows.Media.Brush;
            }
            else
            {
                TempCpuFill.Width = 0;
                TempCpuTag.Foreground = DimText;
                TempCpuTag.ToolTip = cpuHasSensors
                    ? "CPU 温度传感器存在但 BIOS 未暴露数据（部分笔记本限制）"
                    : "未检测到 CPU 温度传感器";
            }

            TempGpuTag.Text = "GPU";
            if (gpuTempAvailable)
            {
                TempGpuTag.ToolTip = null;
                if (TempGpuBarContainer.ActualWidth > 0)
                {
                    TempGpuFill.Width = (gpuTemp / 100.0) * TempGpuBarContainer.ActualWidth;
                    TempGpuFill.Fill = gpuTemp > 80 ? FindResource("RedBar") as System.Windows.Media.Brush
                                     : gpuTemp > 60 ? FindResource("YellowBar") as System.Windows.Media.Brush
                                     : FindResource("YellowBar") as System.Windows.Media.Brush;
                }
                TempGpuTag.Foreground = FindResource("YellowBar") as System.Windows.Media.Brush;
            }
            else
            {
                TempGpuFill.Width = 0;
                TempGpuTag.Foreground = DimText;
                TempGpuTag.ToolTip = gpuHardwareFound
                    ? (gpuHasSensors
                        ? "GPU 温度传感器存在但未返回数据"
                        : "当前 GPU 为集成显卡，无温度传感器")
                    : "未检测到独立 GPU";
            }

            // ── PROC (process count — can be slow) ──
            int procCount = await Task.Run(() =>
            {
                try { return Process.GetProcesses().Length; } catch { return 0; }
            });
            ProcValue.Text = procCount.ToString();
            ProcValue.Foreground = TealText;
            ProcFill.Width = Math.Min(1, procCount / 300.0) * ProcBarContainer.ActualWidth;

            // ── WIFI (WMI/netsh — both blocking) ──
            _wifiTickCounter++;
            int wifiInterval = _wifiWmiAvailable ? 5 : 30;
            if (_wifiTickCounter >= wifiInterval)
            {
                _wifiTickCounter = 0;
                await UpdateWifiSignalAsync();
            }

            // ── BAT (battery — cheap WinForms API, no blocking) ──
            UpdateBattery();
        }
        finally
        {
            _slowMetricsInProgress = false;
        }
    }

    private bool _pingInProgress;  // prevent overlapping async pings

    private async void UpdatePing()
    {
        if (_pingInProgress) return;  // skip if previous ping hasn't returned
        _pingInProgress = true;
        long pingMs = -1;
        try
        {
            var reply = await _pingSender.SendPingAsync(_config.Data.PingTarget, 2000);
            if (reply.Status == IPStatus.Success)
                pingMs = reply.RoundtripTime;
        }
        catch { }
        _pingInProgress = false;

        if (pingMs >= 0)
        {
            PingValue.Text = $"{pingMs}ms";
            PingFill.Width = Math.Min(1, pingMs / 200.0) * PingBarContainer.ActualWidth;
            if (pingMs > 100)
            {
                PingFill.Fill = FindResource("RedBar") as System.Windows.Media.Brush;
                PingValue.Foreground = RedText;
            }
            else if (pingMs > 50)
            {
                PingFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
                PingValue.Foreground = YellowText;
            }
            else
            {
                PingFill.Fill = FindResource("GreenBar") as System.Windows.Media.Brush;
                PingValue.Foreground = FindResource("GreenBar") as System.Windows.Media.Brush;
            }
        }
        else
        {
            PingValue.Text = "N/A";
            PingValue.Foreground = DimText;
            PingFill.Width = 0;
        }
    }

    private async void UpdateIpDnsAsync()
    {
        try
        {
            var (localIps, dnsServer) = await Task.Run(() =>
            {
                string ips = "N/A";
                string dns = "N/A";
                try
                {
                    var candidates = new List<(NetworkInterface ni, System.Net.IPAddress ip, System.Net.IPAddress? dns, bool hasGateway)>();

                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                        string name = ni.Name;
                        string desc = ni.Description;
                        if (name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("6to4", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VMnet", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var ipProps = ni.GetIPProperties();
                        var unicast = ipProps.UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                              && !a.Address.ToString().StartsWith("169."));
                        if (unicast == null) continue;

                        var dnsAddr = ipProps.DnsAddresses
                            .FirstOrDefault(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                        bool hasGateway = ipProps.GatewayAddresses
                            .Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                        candidates.Add((ni, unicast.Address, dnsAddr, hasGateway));
                    }

                    if (candidates.Count > 0)
                    {
                        var primary = candidates.FirstOrDefault(c => c.hasGateway);
                        if (primary.ni == null) primary = candidates[0];
                        if (primary.dns != null) dns = primary.dns.ToString();

                        var ordered = candidates
                            .OrderByDescending(c => c.hasGateway)
                            .ThenBy(c => c.ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                            .Select(c => c.ip.ToString())
                            .ToList();
                        ips = string.Join(" / ", ordered);
                    }
                }
                catch { }
                return (ips, dns);
            });

            AddrValue.Text = localIps;
            DnsValue.Text = dnsServer;
        }
        catch { }
    }

    private async void UpdateUptimeAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var bootTimeStr = obj["LastBootUpTime"]?.ToString();
                    if (bootTimeStr == null) continue;
                    var bootTime = ManagementDateTimeConverter.ToDateTime(bootTimeStr);
                    return (DateTime?)bootTime;
                }
                return null;
            });

            if (result.HasValue)
            {
                var span = DateTime.Now - result.Value;
                var parts = new List<string>();
                if (span.Days > 0) parts.Add($"{span.Days}d");
                parts.Add($"{span.Hours}h");
                parts.Add($"{span.Minutes}m");
                UptimeLabel.Text = "(" + string.Join("", parts) + ")";
            }
        }
        catch { }
    }

    private async Task UpdateWifiSignalAsync()
    {
        // Strategy: Try WMI first (doesn't trigger location service).
        // If WMI works, use it as primary (refreshed every 5s).
        // If WMI fails, fall back to netsh (refreshed every 30s to avoid location icon flash).
        // Both methods are offloaded to background thread to avoid UI freeze.
        if (!_wifiWmiTested || _wifiWmiAvailable)
        {
            int? wmiResult = await Task.Run(() => TryReadWifiViaWmi());
            if (wmiResult.HasValue)
            {
                _wifiWmiAvailable = true;
                _wifiWmiTested = true;
                ApplyWifiSignalDbm(wmiResult.Value);
                return;
            }
            _wifiWmiAvailable = false;
            _wifiWmiTested = true;
        }

        // Fallback: netsh wlan show interfaces (async)
        int? netshResult = await Task.Run(() => TryReadWifiViaNetsh());
        if (netshResult.HasValue)
            ApplyWifiSignalDbm(netshResult.Value);
        else
            SetWifiNA();
    }

    private int? TryReadWifiViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\wmi",
                "SELECT Ndis80211ReceivedSignalStrength FROM MSNdis_80211_ReceivedSignalStrength");
            foreach (ManagementObject obj in searcher.Get())
            {
                var signal = obj["Ndis80211ReceivedSignalStrength"];
                if (signal != null)
                {
                    int dbm = Convert.ToInt32(signal);
                    if (dbm >= -100 && dbm <= 0)
                        return dbm;
                }
            }
        }
        catch { }
        return null;
    }

    private int? TryReadWifiViaNetsh()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (!output.Contains("connected", StringComparison.OrdinalIgnoreCase))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"(?:信号|Signal)\s*:\s*(\d+)%",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int percent = int.Parse(match.Groups[1].Value);
                return (percent / 2) - 100;
            }
        }
        catch { }
        return null;
    }

    private void ApplyWifiSignalDbm(int dbm)
    {
        // Convert dBm back to percentage for bar fill: percent = 2*(dBm+100)
        int percent = Math.Clamp(2 * (dbm + 100), 0, 100);
        WifiValue.Text = $"{dbm}dBm";
        if (WifiBarContainer.ActualWidth > 0)
            WifiFill.Width = (percent / 100.0) * WifiBarContainer.ActualWidth;

        // Color: <25%=red, <50%=yellow, >=50%=cyan
        if (percent < 25)
        {
            WifiFill.Fill = FindResource("RedBar") as System.Windows.Media.Brush;
            WifiValue.Foreground = RedText;
        }
        else if (percent < 50)
        {
            WifiFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
            WifiValue.Foreground = YellowText;
        }
        else
        {
            WifiFill.Fill = FindResource("CyanBar") as System.Windows.Media.Brush;
            WifiValue.Foreground = CyanText;
        }
    }

    private void SetWifiNA()
    {
        WifiValue.Text = "N/A";
        WifiValue.Foreground = DimText;
        WifiFill.Width = 0;
    }

    // ── Battery ──
    private void DetectBattery()
    {
        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            _hasBattery = status.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
        }
        catch { _hasBattery = false; }

        // No battery (desktop) → always hide, ignore user visibility setting
        if (!_hasBattery)
            BatSection.Visibility = Visibility.Collapsed;
    }

    private void UpdateBattery()
    {
        if (!_hasBattery) return;

        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            double percent = Math.Round(status.BatteryLifePercent * 100, 0);
            bool charging = (status.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.Charging) != 0;

            // Value text: "86%" or "86% ⚡"
            BatValue.Text = charging ? $"{percent:F0}% ⚡" : $"{percent:F0}%";

            // Bar fill
            if (BatBarContainer.ActualWidth > 0)
                BatFill.Width = (percent / 100.0) * BatBarContainer.ActualWidth;

            // Color by level: green=full, yellow=medium, red=low
            if (percent < 20)
            {
                BatFill.Fill = FindResource("RedBar") as System.Windows.Media.Brush;
                BatValue.Foreground = RedText;
            }
            else if (percent < 50)
            {
                BatFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
                BatValue.Foreground = YellowText;
            }
            else
            {
                BatFill.Fill = FindResource("GreenBar") as System.Windows.Media.Brush;
                BatValue.Foreground = FindResource("GreenBar") as System.Windows.Media.Brush;
            }
        }
        catch { }
    }

    private static string FmtSpeed(double kbps)
    {
        if (kbps >= 1024) return (kbps / 1024).ToString("F1") + " MB/s";
        return kbps.ToString("F1") + " KB/s";
    }

    // ── GPU ──
    private void InitializeGpuCounters()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("Idd", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mirror", StringComparison.OrdinalIgnoreCase))
                    continue;
                _gpuName = name;
                var vram = obj["AdapterRAM"];
                if (vram != null) _gpuVramMB = Convert.ToDouble(vram) / (1024.0 * 1024.0);
                break;
            }
        }
        catch { }

        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames();
            foreach (var inst in instances)
            {
                if (!inst.Contains("engtype_3D")) continue;
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    counter.NextValue();
                    _gpuCounters.Add(counter);
                }
                catch { }
            }
            _gpuCountersInitialized = _gpuCounters.Count > 0;
        }
        catch { }
    }

    // ── Network ──
    private void InitializeNetworkCounters()
    {
        RescanNetworkAdapters();
    }

    private void RescanNetworkAdapters()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            var instances = category.GetInstanceNames();
            var candidates = new List<(string name, PerformanceCounter sent, PerformanceCounter recv, float recvBytes)>();

            foreach (var inst in instances)
            {
                if (inst.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                    inst.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
                    inst.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var sentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst);
                    var recvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst);
                    sentCounter.NextValue();
                    float recv = recvCounter.NextValue();
                    candidates.Add((inst, sentCounter, recvCounter, recv));
                }
                catch { continue; }
            }

            if (candidates.Count > 0)
            {
                var best = candidates[0];
                foreach (var c in candidates)
                {
                    if (c.name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                        c.name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                        c.name.Contains("WLAN", StringComparison.OrdinalIgnoreCase))
                    { best = c; break; }
                }
                _netSentCounter = best.sent;
                _netReceivedCounter = best.recv;
                _netAdapterName = best.name;
                _netCountersInitialized = true;
            }
        }
        catch { }
    }

    // ── Memory ──
    private static float GetTotalPhysicalMemory()
    {
        try
        {
            var memStatus = new NativeMethods.MEMORYSTATUSEX();
            if (NativeMethods.GlobalMemoryStatusEx(memStatus))
                return memStatus.ullTotalPhys / 1024f / 1024f;
        }
        catch { }
        return 0;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
