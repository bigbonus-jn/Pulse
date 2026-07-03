using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Management;
using LibreHardwareMonitor.Hardware;

namespace DesktopWidgets;

public partial class MainWindow : Window
{
    private NotifyIconWrapper? _notifyIcon;
    private readonly DispatcherTimer _dataTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PerformanceCounter _cpuCounter = new("Processor", "% Processor Time", "_Total");
    private readonly PerformanceCounter _memCounter = new("Memory", "Available MBytes");
    private readonly ConfigService _config = new();
    private bool _cpuFirstSample = true;
    private bool _isCompact;
    private int _slowTickCounter;  // counts 1s ticks for 5s slow metrics

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

    // Pre-allocated colors & brushes (avoid GC per-tick)
    private static readonly SolidColorBrush CyanText = new(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9));
    private static readonly SolidColorBrush MagentaText = new(System.Windows.Media.Color.FromRgb(0xFF, 0x2A, 0x6D));
    private static readonly SolidColorBrush YellowText = new(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush WhiteText = new(System.Windows.Media.Color.FromRgb(0xE6, 0xFF, 0xFF));
    private static readonly SolidColorBrush PurpleText = new(System.Windows.Media.Color.FromRgb(0xBD, 0x93, 0xF9));
    private static readonly SolidColorBrush RedText = new(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush TealText = new(System.Windows.Media.Color.FromRgb(0x00, 0xCE, 0xD1));

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

        // Sync auto-start state from config to registry
        var autoStart = new AutoStartService();
        if (_config.Data.AutoStart && !autoStart.IsEnabled) autoStart.Enable();
        if (!_config.Data.AutoStart && autoStart.IsEnabled) autoStart.Disable();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
        _config.Save();
        _dataTimer.Stop();
        _notifyIcon?.Dispose();
        try { _computer.Close(); } catch { }
    }

    // ── Drag ──
    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
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
        TempValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        ProcValue.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;

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
            TempSection.Margin = new Thickness(0, 0, 0, 2);
            ProcSection.Margin = new Thickness(0, 0, 0, 0);
        }
        else
        {
            // Normal: restore 2-row layout
            SetMetricGridNormal(CpuSection, CpuHeaderRow, CpuBarContainer, CpuLabel);
            SetMetricGridNormal(RamSection, RamHeaderRow, RamBarContainer, RamLabel);
            SetMetricGridNormal(GpuSection, GpuHeaderRow, GpuBarContainer, GpuLabel);
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
            TempSection.Margin = new Thickness(0, 0, 0, 7);
            ProcSection.Margin = new Thickness(0, 0, 0, 0);
        }
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

        // Slow metrics (every 5s): DISK, TEMP, PROC
        _slowTickCounter++;
        if (_slowTickCounter >= 5)
        {
            _slowTickCounter = 0;
            UpdateSlowMetrics();
        }
    }

    private void UpdateSlowMetrics()
    {
        // DISK (reserved — data collection kept for future re-enable)
        // double diskPercent = 0;
        // string diskInfo = "N/A";
        // try
        // {
        //     var drive = System.IO.DriveInfo.GetDrives()
        //         .FirstOrDefault(d => d.IsReady && d.RootDirectory.FullName == "C:\\");
        //     if (drive != null)
        //     {
        //         double diskTotalGB = Math.Round(drive.TotalSize / (1024.0 * 1024 * 1024), 0);
        //         double diskUsedGB = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 * 1024 * 1024), 1);
        //         diskPercent = Math.Round((1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100, 1);
        //         diskInfo = $"{diskUsedGB:F0}/{diskTotalGB:F0} GB";
        //     }
        // }
        // catch { }

        // TEMP (CPU / GPU dual temperature via LibreHardwareMonitor)
        // CPU priority: Package → any core → Storage composite (SSD as proxy) → any temp
        // GPU priority: GpuCore → any GPU temp → none (integrated GPUs often lack sensors)
        double cpuTemp = 0; bool cpuTempAvailable = false; string cpuTempSource = "";
        double gpuTemp = 0; bool gpuTempAvailable = false; string gpuTempSource = "";
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                        continue;

                    // CPU temperature: Package → any core
                    if (hardware.HardwareType == HardwareType.Cpu && !cpuTempAvailable)
                    {
                        if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuTemp = Math.Round(sensor.Value.Value, 0);
                            cpuTempAvailable = true;
                            cpuTempSource = "CPU";
                        }
                        else if (!sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                                 && string.IsNullOrEmpty(cpuTempSource))
                        {
                            cpuTemp = Math.Round(sensor.Value.Value, 0);
                            cpuTempAvailable = true;
                            cpuTempSource = "CPU";
                        }
                    }

                    // GPU temperature: Core → any GPU temp
                    if (!gpuTempAvailable &&
                        (hardware.HardwareType == HardwareType.GpuNvidia ||
                         hardware.HardwareType == HardwareType.GpuAmd ||
                         hardware.HardwareType == HardwareType.GpuIntel))
                    {
                        if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            gpuTemp = Math.Round(sensor.Value.Value, 0);
                            gpuTempAvailable = true;
                            gpuTempSource = "GPU";
                        }
                        else if (!sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                                 && string.IsNullOrEmpty(gpuTempSource))
                        {
                            gpuTemp = Math.Round(sensor.Value.Value, 0);
                            gpuTempAvailable = true;
                            gpuTempSource = "GPU";
                        }
                    }

                    // Storage composite as CPU fallback (SSD temp ≈ system temp proxy)
                    if (hardware.HardwareType == HardwareType.Storage &&
                        sensor.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase) &&
                        !cpuTempAvailable)
                    {
                        cpuTemp = Math.Round(sensor.Value.Value, 0);
                        cpuTempAvailable = true;
                        cpuTempSource = "SSD";
                    }

                    // Any temperature as last-resort CPU fallback
                    if (!cpuTempAvailable)
                    {
                        cpuTemp = Math.Round(sensor.Value.Value, 0);
                        cpuTempAvailable = true;
                        cpuTempSource = "SYS";
                    }
                }
                if (cpuTempAvailable && cpuTempSource == "CPU" && gpuTempAvailable) break;
            }
        }
        catch { }

        // Update TEMP display
        string cpuTempStr = cpuTempAvailable ? $"{cpuTemp:F0}°" : "N/A";
        string gpuTempStr = gpuTempAvailable ? $"{gpuTemp:F0}°" : "N/A";
        TempValue.Text = $"{cpuTempStr} / {gpuTempStr}";

        // Update tag labels to show source when it's not the expected sensor
        TempCpuTag.Text = cpuTempSource == "SSD" ? "SSD" : cpuTempSource == "SYS" ? "SYS" : "CPU";
        TempGpuTag.Text = gpuTempAvailable ? "GPU" : "GPU";

        // CPU temp bar
        if (cpuTempAvailable && TempCpuBarContainer.ActualWidth > 0)
        {
            TempCpuFill.Width = (cpuTemp / 100.0) * TempCpuBarContainer.ActualWidth;
            if (cpuTemp > 80)
                TempCpuFill.Fill = FindResource("RedBar") as System.Windows.Media.Brush;
            else if (cpuTemp > 60)
                TempCpuFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
            else
                TempCpuFill.Fill = FindResource("CyanBar") as System.Windows.Media.Brush;
        }
        else
        {
            TempCpuFill.Width = 0;
        }

        // GPU temp bar
        if (gpuTempAvailable && TempGpuBarContainer.ActualWidth > 0)
        {
            TempGpuFill.Width = (gpuTemp / 100.0) * TempGpuBarContainer.ActualWidth;
            if (gpuTemp > 80)
                TempGpuFill.Fill = FindResource("RedBar") as System.Windows.Media.Brush;
            else if (gpuTemp > 60)
                TempGpuFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
            else
                TempGpuFill.Fill = FindResource("YellowBar") as System.Windows.Media.Brush;
        }
        else
        {
            TempGpuFill.Width = 0;
        }

        // PROC (process count)
        int procCount = 0;
        try { procCount = Process.GetProcesses().Length; } catch { }
        ProcValue.Text = procCount.ToString();
        ProcValue.Foreground = TealText;
        ProcFill.Width = Math.Min(1, procCount / 300.0) * ProcBarContainer.ActualWidth;
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
