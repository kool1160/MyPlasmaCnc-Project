using System.Runtime.InteropServices;
using System.Windows;
using MyPlasm.Inspector.Core.Safety;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.D2xx;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.App;

public partial class MainWindow : Window
{
    private readonly StartupLog _startupLog;
    private readonly ManualInspectionController _inspectionController;

    internal MainWindow(StartupLog startupLog, bool softwareRenderingActive)
    {
        _startupLog = startupLog ?? throw new ArgumentNullException(nameof(startupLog));
        _startupLog.Stage("MainWindow constructor entered before InitializeComponent.");
        InitializeComponent();
        _startupLog.Stage("MainWindow InitializeComponent completed.");

        _inspectionController = new ManualInspectionController(
            static () => new FakeFtdiTransport(),
            static () => D2xxInspectionTransport.CreateDefault());

        RenderingStatusText.Text = softwareRenderingActive
            ? "Software rendering active (safe default)."
            : "Hardware rendering enabled by --hardware-rendering.";
        ArchitectureStatusText.Text = $"Process architecture: {RuntimeInformation.ProcessArchitecture}";
        AllowlistStatusText.Text = $"Production command allowlist: {new DenyByDefaultCommandSafetyPolicy().AllowedCommandCount} entries (empty)";
        LogFileLocationText.Text = _startupLog.FilePath;
        AddEvent("Startup-safe window initialized. No transport has been created or enumerated.");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _startupLog.Stage("MainWindow Loaded event completed without transport activity.");
        AddEvent("Choose a manual action to create an inspection transport.");
    }

    private async void RunFakeEnumerationButton_Click(object sender, RoutedEventArgs e)
    {
        await RunEnumerationAsync(
            "Fake enumeration",
            _inspectionController.RunFakeEnumerationAsync,
            false);
    }

    private async void InspectD2xxDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunEnumerationAsync(
            "D2XX inspection",
            _inspectionController.InspectD2xxDevicesAsync,
            true);
    }

    private async Task RunEnumerationAsync(
        string actionName,
        Func<CancellationToken, ValueTask<IReadOnlyList<FtdiDeviceInfo>>> action,
        bool d2xxInspection)
    {
        RunFakeEnumerationButton.IsEnabled = false;
        InspectD2xxDevicesButton.IsEnabled = false;
        InspectionStatusText.Text = $"{actionName} running...";
        _startupLog.Stage($"Operator requested {actionName}.");

        try
        {
            IReadOnlyList<FtdiDeviceInfo> devices = await action(CancellationToken.None);
            int candidates = devices.Count(device => device.IsMyPlasmController);
            InspectionStatusText.Text = $"{actionName}: {devices.Count} device(s); {candidates} exact MyPlasm candidate(s).";
            AddEvent($"{actionName} completed without opening a device or transmitting bytes.");

            if (d2xxInspection && _inspectionController.CurrentTransport is D2xxInspectionTransport d2xx)
            {
                foreach (D2xxDiagnostic diagnostic in d2xx.Diagnostics)
                {
                    AddEvent($"{diagnostic.Severity}: {diagnostic.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            InspectionStatusText.Text = $"{actionName} failed. See startup log.";
            _startupLog.Exception(actionName, exception);
            AddEvent($"{actionName} error: {exception.Message}");
        }
        finally
        {
            RunFakeEnumerationButton.IsEnabled = true;
            InspectD2xxDevicesButton.IsEnabled = true;
        }
    }

    private void AddEvent(string message)
    {
        EventLog.Items.Insert(0, $"{DateTimeOffset.Now:T}  {message}");
        _startupLog.Stage($"UI: {message}");
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            _startupLog.Stage("MainWindow closing; disposing any manually created transport.");
            await _inspectionController.DisposeAsync();
        }
        catch (Exception exception)
        {
            _startupLog.Exception("MainWindow closed", exception);
        }
    }
}
