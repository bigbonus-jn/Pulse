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
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_crash.txt"),
                args.Exception.ToString());
            args.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_crash2.txt"),
                args.ExceptionObject?.ToString() ?? "unknown");
        };
    }
}

