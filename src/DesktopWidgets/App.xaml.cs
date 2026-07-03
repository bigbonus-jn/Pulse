using System.Configuration;
using System.Data;
using System.Windows;

namespace DesktopWidgets;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        this.DispatcherUnhandledException += (s, args) =>
        {
            // Log the error but don't shut down — a transient exception in
            // PerformanceCounter.NextValue() (e.g. GPU instance vanished)
            // should not kill the whole app.
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] {args.ExceptionObject}\n\n");
        };
    }
}

