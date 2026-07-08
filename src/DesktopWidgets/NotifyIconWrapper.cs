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
        menu.Items.Add("Show / Hide", null, OnToggleVisibility);
        menu.Items.Add("Toggle Click-Through", null, OnToggleClickThrough);
        menu.Items.Add("Toggle Compact Mode", null, OnToggleCompact);
        menu.Items.Add(CreateAutoStartMenuItem());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, OnExit);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += OnToggleVisibility;
    }

    private System.Windows.Forms.ToolStripMenuItem CreateAutoStartMenuItem()
    {
        var item = new System.Windows.Forms.ToolStripMenuItem("Auto-Start on Boot");
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

    private void OnToggleClickThrough(object? sender, EventArgs e)
    {
        _mainWindow.ToggleClickThrough();
    }

    private void OnToggleCompact(object? sender, EventArgs e)
    {
        _mainWindow.ToggleCompactMode();
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
