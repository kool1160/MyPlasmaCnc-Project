using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyPlasm.Inspector.App;

public partial class App : Application
{
    private readonly StartupLog _startupLog;

    public App()
    {
        _startupLog = StartupLog.Create();
        _startupLog.Stage("App constructor entered.");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _startupLog.Stage("OnStartup entered.");

        try
        {
            bool hardwareRenderingRequested = e.Args.Any(
                argument => string.Equals(argument, "--hardware-rendering", StringComparison.OrdinalIgnoreCase));
            bool softwareRenderingActive = !hardwareRenderingRequested;
            RenderOptions.ProcessRenderMode = softwareRenderingActive
                ? RenderMode.SoftwareOnly
                : RenderMode.Default;

            _startupLog.Stage(
                softwareRenderingActive
                    ? "WPF software rendering forced before MainWindow construction."
                    : "WPF hardware rendering requested before MainWindow construction.");
            _startupLog.WriteEnvironment(softwareRenderingActive);

            base.OnStartup(e);
            _startupLog.Stage("Base application startup completed.");

            MainWindow window = new(_startupLog, softwareRenderingActive);
            MainWindow = window;
            _startupLog.Stage("MainWindow constructed; showing startup-safe window.");
            window.Show();
            _startupLog.Stage("Startup-safe window shown. No transport was created or enumerated.");
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Application startup", exception);
            MessageBox.Show(
                $"MyPlasm Inspector could not start. See:{Environment.NewLine}{_startupLog.FilePath}",
                "MyPlasm Inspector startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupLog.Stage($"Application exit requested with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _startupLog.Exception("Application.DispatcherUnhandledException", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _startupLog.Exception("AppDomain.CurrentDomain.UnhandledException", exception);
            return;
        }

        _startupLog.Stage($"AppDomain.CurrentDomain.UnhandledException: {e.ExceptionObject}");
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _startupLog.Exception("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
