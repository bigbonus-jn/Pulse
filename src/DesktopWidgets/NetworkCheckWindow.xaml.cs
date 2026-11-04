using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pulse;

/// <summary>
/// 网络健康体检窗口 — 逐项检测：信号 / 延迟 / 丢包 / 抖动 / DNS / TTFB / 下载，
/// 给出 0~100 总分、等级和一句人话建议。
/// </summary>
public partial class NetworkCheckWindow : Window
{
    private readonly ConfigService _config;
    private readonly Func<int?> _getRssi;
    private readonly Ping _ping = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private bool _isChecking;
    private CancellationTokenSource? _cts;

    // Pre-allocated brushes
    private static readonly SolidColorBrush CyanText = new(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xF9));
    private static readonly SolidColorBrush YellowText = new(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush RedText = new(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush DimText = new(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly SolidColorBrush WhiteText = new(System.Windows.Media.Color.FromRgb(0xCC, 0xFF, 0xFF));

    // Check row widgets
    private sealed class CheckRow
    {
        public required TextBlock NameLabel;
        public required TextBlock ValueLabel;
        public required System.Windows.Shapes.Rectangle BarFill;
        public required Grid BarTrack;
    }

    private readonly Dictionary<string, CheckRow> _rows = new();

    public NetworkCheckWindow(ConfigService config, Func<int?> getRssi)
    {
        InitializeComponent();
        _config = config;
        _getRssi = getRssi;

        BuildRows();
        Loaded += async (_, _) => await RunChecksAsync();
        SourceInitialized += (_, _) =>
        {
            // Keep it a tool window (no taskbar entry)
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private void BuildRows()
    {
        // key, display name
        var items = new (string Key, string Name)[]
        {
            ("SIGNAL", "无线信号"),
            ("LATENCY", "延迟 DELAY"),
            ("LOSS", "丢包 LOSS"),
            ("JITTER", "抖动 JITTER"),
            ("DNS", "DNS 解析"),
            ("TTFB", "网页响应"),
            ("DOWNLOAD", "下载速度"),
        };

        foreach (var (key, name) in items)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var nameLabel = new TextBlock
            {
                Text = name,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                Foreground = WhiteText,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameLabel, 0);
            grid.Children.Add(nameLabel);

            // Bar track + fill
            var track = new Grid { Height = 5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) });
            var fill = new System.Windows.Shapes.Rectangle
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = 0,
                Fill = CyanText
            };
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            var valueLabel = new TextBlock
            {
                Text = "等待",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                Foreground = DimText,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valueLabel, 2);
            grid.Children.Add(valueLabel);

            CheckList.Children.Add(grid);

            _rows[key] = new CheckRow
            {
                NameLabel = nameLabel,
                ValueLabel = valueLabel,
                BarFill = fill,
                BarTrack = track
            };
        }
    }

    private void SetRowState(string key, string text, System.Windows.Media.Brush brush, double quality /* 0..1 */)
    {
        if (!_rows.TryGetValue(key, out var row)) return;
        row.ValueLabel.Text = text;
        row.ValueLabel.Foreground = brush;
        if (row.BarTrack.ActualWidth > 0)
            row.BarFill.Width = Math.Clamp(quality, 0, 1) * row.BarTrack.ActualWidth;
        row.BarFill.Fill = brush;
    }

    private void SetRowPending(string key, string text = "检测中")
    {
        if (!_rows.TryGetValue(key, out var row)) return;
        row.ValueLabel.Text = $"{text}…";
        row.ValueLabel.Foreground = DimText;
        row.BarFill.Width = 0;
    }

    private void SetRowSkipped(string key, string text = "N/A")
    {
        if (!_rows.TryGetValue(key, out var row)) return;
        row.ValueLabel.Text = text;
        row.ValueLabel.Foreground = DimText;
        row.BarFill.Width = 0;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isChecking) return;
        await RunChecksAsync();
    }

    // ── Main check flow ──
    private async Task RunChecksAsync()
    {
        _isChecking = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        RetryButton.IsEnabled = false;
        ScoreLabel.Text = "--";
        ScoreLabel.Foreground = CyanText;
        GradeLabel.Text = "";
        AdviceLabel.Text = "正在检测网络健康度…";
        StatusLabel.Text = "";
        foreach (var key in _rows.Keys) SetRowPending(key, "等待");

        // Results storage: key -> (score 0..100 or null if skipped, readable text)
        var scores = new Dictionary<string, double?>();
        var texts = new Dictionary<string, string>();

        try
        {
            // ── 1. Signal (instant) ──
            SetRowPending("SIGNAL");
            await Task.Delay(120, ct);
            int? rssi = _getRssi();
            if (rssi.HasValue)
            {
                double s = ScoreSignal(rssi.Value);
                scores["SIGNAL"] = s;
                texts["SIGNAL"] = $"{rssi.Value}dBm";
                SetRowState("SIGNAL", $"{rssi.Value}dBm", BrushForScore(s), s / 100);
            }
            else
            {
                scores["SIGNAL"] = null;
                texts["SIGNAL"] = "N/A";
                SetRowSkipped("SIGNAL", "有线/未检出");
            }

            // ── 2-4. Ping batch: latency / loss / jitter ──
            SetRowPending("LATENCY");
            SetRowPending("LOSS");
            SetRowPending("JITTER");
            var rtts = new List<long>();
            int lost = 0;
            const int totalPings = 20;
            for (int i = 0; i < totalPings; i++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var reply = await _ping.SendPingAsync(_config.Data.PingTarget, 1000);
                    if (reply.Status == IPStatus.Success) rtts.Add(reply.RoundtripTime);
                    else lost++;
                }
                catch { lost++; }
                await Task.Delay(100, ct);
            }

            double lossPct = lost * 100.0 / totalPings;
            double avgLatency = rtts.Count > 0 ? rtts.Average() : -1;
            double jitter = 0;
            if (rtts.Count >= 2)
            {
                double sum = 0;
                for (int i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
                jitter = sum / (rtts.Count - 1);
            }

            // Latency
            if (avgLatency >= 0)
            {
                double s = ScoreLatency(avgLatency);
                scores["LATENCY"] = s;
                texts["LATENCY"] = $"{avgLatency:0}ms";
                SetRowState("LATENCY", $"{avgLatency:0}ms", BrushForScore(s), s / 100);
            }
            else
            {
                scores["LATENCY"] = 0;
                texts["LATENCY"] = "超时";
                SetRowState("LATENCY", "超时", RedText, 0);
            }

            // Loss
            {
                double s = ScoreLoss(lossPct);
                scores["LOSS"] = s;
                texts["LOSS"] = $"{lossPct:0}%";
                SetRowState("LOSS", $"{lossPct:0}%", BrushForScore(s), s / 100);
            }

            // Jitter
            {
                double s = ScoreJitter(jitter);
                scores["JITTER"] = s;
                texts["JITTER"] = $"{jitter:0.0}ms";
                SetRowState("JITTER", $"{jitter:0.0}ms", BrushForScore(s), s / 100);
            }

            // ── 5. DNS resolve ──
            SetRowPending("DNS");
            await Task.Delay(100, ct);
            long dnsMs = -1;
            try
            {
                var sw = Stopwatch.StartNew();
                await Dns.GetHostAddressesAsync("www.baidu.com");
                dnsMs = sw.ElapsedMilliseconds;
            }
            catch { }
            if (dnsMs >= 0)
            {
                double s = ScoreDns(dnsMs);
                scores["DNS"] = s;
                texts["DNS"] = $"{dnsMs}ms";
                SetRowState("DNS", $"{dnsMs}ms", BrushForScore(s), s / 100);
            }
            else
            {
                scores["DNS"] = 0;
                texts["DNS"] = "失败";
                SetRowState("DNS", "失败", RedText, 0);
            }

            // ── 6. TTFB (web response) ──
            SetRowPending("TTFB");
            await Task.Delay(100, ct);
            long ttfbMs = -1;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.baidu.com/");
                var sw = Stopwatch.StartNew();
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                ttfbMs = sw.ElapsedMilliseconds;
            }
            catch { }
            if (ttfbMs >= 0)
            {
                double s = ScoreTtfb(ttfbMs);
                scores["TTFB"] = s;
                texts["TTFB"] = $"{ttfbMs}ms";
                SetRowState("TTFB", $"{ttfbMs}ms", BrushForScore(s), s / 100);
            }
            else
            {
                scores["TTFB"] = 0;
                texts["TTFB"] = "失败";
                SetRowState("TTFB", "失败", RedText, 0);
            }

            // ── 7. Download speed (1MB) ──
            SetRowPending("DOWNLOAD");
            await Task.Delay(100, ct);
            double mbps = -1;
            try
            {
                using var resp = await _http.GetAsync(
                    "https://speed.cloudflare.com/__down?bytes=1048576",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var sw = Stopwatch.StartNew();
                var buffer = new byte[65536];
                long totalBytes = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0) totalBytes += read;
                double secs = sw.Elapsed.TotalSeconds;
                if (secs > 0.02 && totalBytes > 65536)
                    mbps = totalBytes * 8 / secs / 1_000_000;
            }
            catch { }
            if (mbps >= 0)
            {
                double s = ScoreDownload(mbps);
                scores["DOWNLOAD"] = s;
                texts["DOWNLOAD"] = $"{mbps:0.0} Mbps";
                SetRowState("DOWNLOAD", $"{mbps:0.0} Mbps", BrushForScore(s), s / 100);
            }
            else
            {
                scores["DOWNLOAD"] = null;  // skip, don't punish
                texts["DOWNLOAD"] = "N/A";
                SetRowSkipped("DOWNLOAD", "不可用");
            }

            // ── Total score ──
            var weights = new Dictionary<string, double>
            {
                ["SIGNAL"] = 15,
                ["LATENCY"] = 25,
                ["LOSS"] = 20,
                ["JITTER"] = 10,
                ["DNS"] = 10,
                ["TTFB"] = 10,
                ["DOWNLOAD"] = 10,
            };

            double weightedSum = 0, weightTotal = 0;
            foreach (var kv in scores)
            {
                if (kv.Value == null) continue;  // skipped items excluded
                weightedSum += kv.Value.Value * weights[kv.Key];
                weightTotal += weights[kv.Key];
            }
            double totalScore = weightTotal > 0 ? weightedSum / weightTotal : 0;

            // Animate score up
            int to = (int)Math.Round(totalScore);
            var anim = new System.Windows.Media.Animation.Int32Animation(0, to, TimeSpan.FromMilliseconds(800))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            BeginAnimation(ScoreProperty, anim);

            var (grade, gradeBrush) = GradeFor(totalScore);
            GradeLabel.Text = grade;
            GradeLabel.Foreground = gradeBrush;
            ScoreLabel.Foreground = brushForTotal(totalScore);

            AdviceLabel.Text = BuildAdvice(scores, texts);
            StatusLabel.Text = $"检测完成 · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusLabel.Text = "检测异常：" + ex.Message;
        }
        finally
        {
            _isChecking = false;
            RetryButton.IsEnabled = true;
        }
    }

    private static readonly DependencyProperty ScoreProperty =
        DependencyProperty.Register("ScoreValue", typeof(int), typeof(NetworkCheckWindow),
            new PropertyMetadata(0, (d, e) =>
            {
                if (d is NetworkCheckWindow w)
                    w.ScoreLabel.Text = e.NewValue?.ToString() ?? "0";
            }));

    // ── Scoring helpers ──
    private static double ScoreSignal(int dbm) => dbm switch
    {
        >= -50 => 100,
        >= -60 => 90,
        >= -65 => 80,
        >= -70 => 65,
        >= -75 => 50,
        >= -80 => 35,
        _ => 15
    };

    private static double ScoreLatency(double ms) => ms switch
    {
        <= 20 => 100,
        <= 50 => 90,
        <= 80 => 80,
        <= 120 => 65,
        <= 200 => 45,
        _ => 25
    };

    private static double ScoreLoss(double pct) => pct switch
    {
        <= 0 => 100,
        <= 5 => 85,
        <= 10 => 65,
        <= 20 => 40,
        _ => 10
    };

    private static double ScoreJitter(double ms) => ms switch
    {
        <= 3 => 100,
        <= 10 => 85,
        <= 25 => 65,
        <= 50 => 45,
        _ => 25
    };

    private static double ScoreDns(long ms) => ms switch
    {
        <= 20 => 100,
        <= 50 => 90,
        <= 100 => 80,
        <= 200 => 60,
        _ => 35
    };

    private static double ScoreTtfb(long ms) => ms switch
    {
        <= 150 => 100,
        <= 400 => 85,
        <= 800 => 70,
        <= 1500 => 50,
        _ => 30
    };

    private static double ScoreDownload(double mbps) => mbps switch
    {
        >= 100 => 100,
        >= 50 => 90,
        >= 20 => 75,
        >= 10 => 60,
        >= 5 => 45,
        _ => 30
    };

    private static System.Windows.Media.Brush BrushForScore(double s) =>
        s >= 75 ? CyanText : s >= 50 ? YellowText : RedText;

    private static System.Windows.Media.Brush brushForTotal(double s) =>
        s >= 75 ? CyanText : s >= 50 ? YellowText : RedText;

    private static (string Grade, System.Windows.Media.Brush Brush) GradeFor(double s) =>
        s >= 90 ? ("S", CyanText) :
        s >= 80 ? ("A", CyanText) :
        s >= 70 ? ("B", YellowText) :
        s >= 60 ? ("C", YellowText) :
        ("D", RedText);

    private string BuildAdvice(Dictionary<string, double?> scores, Dictionary<string, string> texts)
    {
        // Find the weakest measurable item
        string? weakestKey = null;
        double weakest = 101;
        foreach (var kv in scores)
        {
            if (kv.Value == null) continue;
            if (kv.Value.Value < weakest) { weakest = kv.Value.Value; weakestKey = kv.Key; }
        }

        if (weakestKey == null) return "未能完成检测，请检查网络连接后重试。";
        if (weakest >= 85) return "网络状态良好，适合游戏、视频和日常使用。";
        if (weakest >= 70)
            return weakestKey switch
            {
                "SIGNAL" => "整体不错，但信号一般，离路由器远时可能变慢。",
                "LATENCY" => "整体不错，延迟略高，游戏时可能感觉轻微迟滞。",
                "LOSS" => "整体不错，但偶有丢包，视频通话可能短暂卡顿。",
                "JITTER" => "整体不错，延迟有波动，实时对战可能偶发跳 ping。",
                "DNS" => "整体不错，DNS 解析稍慢，网页首次打开略慢。",
                "TTFB" => "整体不错，网页首次响应稍慢。",
                "DOWNLOAD" => "整体不错，下载速度一般。",
                _ => "整体不错。"
            };
        return weakestKey switch
        {
            "SIGNAL" => "信号偏弱是主要问题，建议靠近路由器或更换信道。",
            "LATENCY" => "延迟偏高，可能有设备占满带宽，或线路拥塞。",
            "LOSS" => "存在明显丢包，网络不稳定，建议检查无线干扰或线路质量。",
            "JITTER" => "延迟波动大，视频会议、游戏容易卡顿。",
            "DNS" => "DNS 解析慢，可尝试更换为 223.5.5.5 或 114.114.114.114。",
            "TTFB" => "网页响应慢，可能是出口线路或远端服务器问题。",
            "DOWNLOAD" => "下载速度偏慢，建议确认带宽套餐或路由器能力。",
            _ => "网络存在瓶颈，建议进一步排查。"
        };
    }

    // ── Window chrome ──
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}