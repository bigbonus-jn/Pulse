using System.Drawing;
using System.Windows;

namespace Pulse;

/// <summary>
/// System tray icon with context menu.
/// Note: Uses System.Drawing.Common for NotifyIcon (WinForms).
/// </summary>
public class NotifyIconWrapper : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly MainWindow _mainWindow;
    private readonly AutoStartService _autoStart = new();
    private System.Windows.Forms.ToolStripMenuItem? _compactMenuItem;

    public NotifyIconWrapper(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Text = "Pulse / 脉搏",
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, OnToggleVisibility);
        _compactMenuItem = CreateCompactMenuItem();
        menu.Items.Add(_compactMenuItem);
        menu.Items.Add(CreateAutoStartMenuItem());
        menu.Items.Add("-");
        menu.Items.Add("退出", null, OnExit);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += OnToggleVisibility;
    }

    private System.Windows.Forms.ToolStripMenuItem CreateCompactMenuItem()
    {
        var item = new System.Windows.Forms.ToolStripMenuItem("简洁模式");
        item.Checked = _mainWindow.IsCompact;
        item.Click += OnToggleCompact;
        return item;
    }

    public void SyncCompactState(bool isCompact)
    {
        if (_compactMenuItem != null)
            _compactMenuItem.Checked = isCompact;
    }

    private System.Windows.Forms.ToolStripMenuItem CreateAutoStartMenuItem()
    {
        var item = new System.Windows.Forms.ToolStripMenuItem("开机自启");
        item.Checked = _autoStart.IsEnabled;
        item.Click += OnToggleAutoStart;
        return item;
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        var item = sender as System.Windows.Forms.ToolStripMenuItem;
        if (item == null) return;

        bool newState = !item.Checked;
        _autoStart.SetEnabled(newState);
        item.Checked = _autoStart.IsEnabled;

        // Persist to config
        _mainWindow.SetAutoStartConfig(_autoStart.IsEnabled);
    }

    private static Icon CreateDefaultIcon()
    {
        // Load embedded pulse.ico
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("Pulse.pulse.ico");
        if (stream != null)
            return new Icon(stream);

        // Fallback: simple blue circle
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.FillEllipse(Brushes.DodgerBlue, 1, 1, 14, 14);
        g.FillEllipse(Brushes.White, 5, 5, 6, 6);
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private void OnToggleVisibility(object? sender, EventArgs e)
    {
        if (_mainWindow.IsVisible)
            _mainWindow.Hide();
        else
            _mainWindow.ShowWindow();
    }

    private void OnToggleCompact(object? sender, EventArgs e)
    {
        _mainWindow.ToggleCompactMode();
        if (_compactMenuItem != null)
            _compactMenuItem.Checked = _mainWindow.IsCompact;
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
