using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Management;

namespace DesktopWidgets;

public partial class MainWindow : Window
{
    private NotifyIconWrapper? _notifyIcon;
    private readonly DispatcherTimer _dataTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PerformanceCounter _cpuCounter = new("Processor", "% Processor Time", "_Total");
    private readonly PerformanceCounter _memCounter = new("Memory", "Available MBytes");
    private readonly ConfigService _config = new();
    private bool _isWebViewReady;
    private bool _cpuFirstSample = true;

    // Network monitoring - auto-detect active adapter (includes WiFi)
    private PerformanceCounter? _netSentCounter;
    private PerformanceCounter? _netReceivedCounter;
    private bool _netCountersInitialized;
    private string _netAdapterName = "";
    private int _netRescanCooldown; // frames to wait before rescanning

    // GPU monitoring
    private string _gpuName = "N/A";
    private double _gpuVramMB;
    private List<PerformanceCounter> _gpuCounters = new();
    private bool _gpuCountersInitialized;

    // Win32 API for click-through and window positioning
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Win32 constants for drag
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();

    public MainWindow()
    {
        InitializeComponent();
        _config.Load();
        Left = _config.Data.X;
        Top = _config.Data.Y;
        Width = _config.Data.Width;
        Height = _config.Data.Height;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize system tray
        _notifyIcon = new NotifyIconWrapper(this);

        // Ensure topmost and tool window
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        // Initialize WebView2
        await WebView.EnsureCoreWebView2Async();

        var htmlPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "widgets", "cpu-gauge.html");
        if (System.IO.File.Exists(htmlPath))
        {
            WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        }
        else
        {
            // Fallback: use embedded html from src/widgets
            var srcPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)!,
                "widgets", "cpu-gauge.html");
            // Try project source path
            var devPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "widgets", "cpu-gauge.html");
            devPath = System.IO.Path.GetFullPath(devPath);
            if (System.IO.File.Exists(devPath))
                WebView.CoreWebView2.Navigate(new Uri(devPath).AbsoluteUri);
            else
                WebView.CoreWebView2.NavigateToString("<html><body style='background:#FF00FF;color:white;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;font-family:Segoe UI;'><div>Loading...</div></body></html>");
        }


        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _isWebViewReady = true;

        // Start data push timer
        _dataTimer.Tick += PushSystemData;
        _dataTimer.Start();

        // Initialize network counters (find active adapter including WiFi)
        InitializeNetworkCounters();

        // Initialize GPU monitoring
        InitializeGpuCounters();
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
    }

    private void InitializeGpuCounters()
    {
        try
        {
            // Get GPU name and VRAM via WMI
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                // Skip virtual/display drivers
                if (name.Contains("Idd", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mirror", StringComparison.OrdinalIgnoreCase))
                    continue;
                _gpuName = name;
                var vram = obj["AdapterRAM"];
                if (vram != null) _gpuVramMB = Convert.ToDouble(vram) / (1024.0 * 1024.0);
                break; // Use first real GPU
            }
        }
        catch { }

        try
        {
            // Try GPU Engine performance counter for utilization
            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames();
            foreach (var inst in instances)
            {
                // Only count 3D engines for meaningful utilization
                if (!inst.Contains("engtype_3D")) continue;
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    counter.NextValue(); // First sample
                    _gpuCounters.Add(counter);
                }
                catch { }
            }
            _gpuCountersInitialized = _gpuCounters.Count > 0;
        }
        catch { /* GPU Engine counters not available */ }
    }

    private void InitializeNetworkCounters()
    {
        // Initial scan - will be refined dynamically in PushSystemData
        RescanNetworkAdapters();
    }

    private void RescanNetworkAdapters()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            var instances = category.GetInstanceNames();

            // Collect all valid adapters (skip tunnels)
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
                    // Prime and read first sample
                    sentCounter.NextValue();
                    float recv = recvCounter.NextValue();
                    candidates.Add((inst, sentCounter, recvCounter, recv));
                }
                catch { continue; }
            }

            if (candidates.Count > 0)
            {
                // Pick WiFi first, then the one with most traffic
                var best = candidates[0];
                foreach (var c in candidates)
                {
                    // Prefer WiFi adapters
                    if (c.name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                        c.name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                        c.name.Contains("WLAN", StringComparison.OrdinalIgnoreCase))
                    {
                        best = c;
                        break;
                    }
                }
                _netSentCounter = best.sent;
                _netReceivedCounter = best.recv;
                _netAdapterName = best.name;
                _netCountersInitialized = true;
            }
        }
        catch { /* Network counters not available */ }
    }

    private void PushSystemData(object? sender, EventArgs e)
    {
        if (!_isWebViewReady || WebView.CoreWebView2 == null) return;

        float cpuUsage = _cpuCounter.NextValue();
        // First call to NextValue always returns 0, skip it
        if (_cpuFirstSample) { _cpuFirstSample = false; return; }
        float availableMem = _memCounter.NextValue();
        float totalMem = GetTotalPhysicalMemory();
        float memUsage = totalMem > 0 ? ((totalMem - availableMem) / totalMem) * 100f : 0f;

        // Network speed - with dynamic adapter re-selection
        double netSentKbps = 0, netRecvKbps = 0;
        if (_netCountersInitialized && _netSentCounter != null && _netReceivedCounter != null)
        {
            try
            {
                float sentBytes = _netSentCounter.NextValue();
                float recvBytes = _netReceivedCounter.NextValue();
                netSentKbps = Math.Round(sentBytes / 1024.0, 1);
                netRecvKbps = Math.Round(recvBytes / 1024.0, 1);

                // If both are zero for a while, rescan (WiFi may have reconnected)
                if (sentBytes == 0 && recvBytes == 0)
                {
                    _netRescanCooldown++;
                    if (_netRescanCooldown >= 5) // 5 seconds of no traffic → rescan
                    {
                        _netRescanCooldown = 0;
                        RescanNetworkAdapters();
                    }
                }
                else
                {
                    _netRescanCooldown = 0;
                }
            }
            catch
            {
                // Counter became invalid (adapter disconnected), rescan
                RescanNetworkAdapters();
            }
        }
        else if (!_netCountersInitialized)
        {
            // Never initialized - try again
            _netRescanCooldown++;
            if (_netRescanCooldown >= 3)
            {
                _netRescanCooldown = 0;
                RescanNetworkAdapters();
            }
        }

        // GPU utilization
        double gpuUsage = 0;
        if (_gpuCountersInitialized)
        {
            float total = 0;
            foreach (var c in _gpuCounters)
                total += c.NextValue();
            gpuUsage = Math.Round(Math.Min(100, total), 1);
        }

        var data = new
        {
            type = "systemData",
            cpu = Math.Round(cpuUsage, 1),
            memory = Math.Round(memUsage, 1),
            memoryTotal = Math.Round(totalMem, 0),
            memoryUsed = Math.Round(totalMem - availableMem, 0),
            gpu = gpuUsage,
            gpuName = _gpuName,
            netUp = netSentKbps,
            netDown = netRecvKbps,
            date = DateTime.Now.ToString("yyyy-MM-dd"),
            time = DateTime.Now.ToString("HH:mm:ss")
        };

        string json = JsonSerializer.Serialize(data);
        WebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (message == "toggleClickThrough")
        {
            ToggleClickThrough();
        }
        else if (message == "startDrag")
        {
            // Initiate window drag from JS notification
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
    }

    public void ToggleClickThrough()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_EX_TRANSPARENT) != 0)
        {
            // Remove click-through
            SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
        }
        else
        {
            // Add click-through
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
        }
    }

    public void ToggleCompactMode()
    {
        if (!_isWebViewReady || WebView.CoreWebView2 == null) return;
        WebView.CoreWebView2.ExecuteScriptAsync(
            "toggleCompactFromCS()");
    }

    public void ShowWindow()
    {
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static float GetTotalPhysicalMemory()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            var memStatus = new NativeMethods.MEMORYSTATUSEX();
            if (NativeMethods.GlobalMemoryStatusEx(memStatus))
                return memStatus.ullTotalPhys / 1024f / 1024f; // MB
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
