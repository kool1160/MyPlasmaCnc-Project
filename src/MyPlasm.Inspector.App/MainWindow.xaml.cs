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
    private PassiveCaptureResult? _lastCapture;

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

    private async void OpenExactDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectionController.CurrentTransport is not D2xxInspectionTransport d2xx)
        {
            AddEvent("Inspect D2XX Devices before opening."); return;
        }
        MessageBoxResult confirmation = MessageBox.Show("Close the original MyPlasm software. Keep 48 V motor power and plasma power off; disconnect torch start. This performs no writes but temporarily holds the FTDI device open.", "Passive receive safety confirmation", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        try
        {
            await d2xx.OpenExactPassiveSessionAsync(() => System.Diagnostics.Process.GetProcessesByName("MyPlasmCNC").Length > 0);
            InspectionStatusText.Text = $"Exact device open; driver {d2xx.DriverVersion ?? "unavailable"}; transmit count: 0";
            AddEvent("Exact MyPlasm device opened by enumerated serial only; zero transmits.");
        }
        catch (Exception exception) { _startupLog.Exception("Open exact device", exception); AddEvent(exception.Message); }
    }

    private async void StartCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectionController.CurrentTransport is not D2xxInspectionTransport d2xx || !d2xx.HasOpenPassiveSession) { AddEvent("Open the exact device first."); return; }
        try
        {
            PassiveCaptureResult capture = await d2xx.CapturePassiveReceiveAsync(TimeSpan.FromSeconds(30));
            _lastCapture = capture;
            InspectionStatusText.Text = $"Passive capture stopped: {capture.TotalBytes} bytes; {capture.Chunks.Count} chunks; transmit count: 0";
            AddEvent("Passive receive capture completed; zero transmits.");
        }
        catch (Exception exception) { _startupLog.Exception("Passive capture", exception); AddEvent(exception.Message); }
    }

    private async void CloseDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectionController.CurrentTransport is D2xxInspectionTransport d2xx) await d2xx.ClosePassiveSessionAsync();
        InspectionStatusText.Text = "Device closed; transmit count: 0";
        AddEvent("Passive device session closed.");
    }

    private void ExportDiagnosticPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCapture is null) { AddEvent("Run a passive capture before export."); return; }
        try { AddEvent($"Capture ZIP exported: {CaptureExporter.Export(_lastCapture, _startupLog)}"); }
        catch (Exception exception) { _startupLog.Exception("Capture export", exception); AddEvent(exception.Message); }
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
