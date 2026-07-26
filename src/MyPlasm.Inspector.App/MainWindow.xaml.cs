using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MyPlasm.Inspector.Core.Safety;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.D2xx;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.App;

public partial class MainWindow : Window
{
    private const int MaximumUiEvents = 200;

    private readonly StartupLog _startupLog;
    private readonly ManualInspectionController _inspectionController;
    private readonly bool _softwareRenderingActive;
    private PassiveD2xxSession? _passiveSession;
    private PassiveCaptureService? _captureService;
    private PassiveCaptureResult? _lastCapture;
    private bool _closeReady;

    internal MainWindow(
        StartupLog startupLog,
        bool softwareRenderingActive)
    {
        _startupLog =
            startupLog ?? throw new ArgumentNullException(nameof(startupLog));
        _softwareRenderingActive = softwareRenderingActive;
        _startupLog.Stage(
            "MainWindow constructor entered before InitializeComponent.");
        InitializeComponent();
        _startupLog.Stage("MainWindow InitializeComponent completed.");

        _inspectionController = new ManualInspectionController(
            static () => new FakeFtdiTransport(),
            static () => D2xxInspectionTransport.CreateDefault());

        RenderingStatusText.Text = softwareRenderingActive
            ? "Software rendering active (safe default)."
            : "Hardware rendering enabled by --hardware-rendering.";
        ArchitectureStatusText.Text =
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}";
        AllowlistStatusText.Text =
            "Production command allowlist: " +
            $"{new DenyByDefaultCommandSafetyPolicy().AllowedCommandCount} entries (empty)";
        LogFileLocationText.Text = _startupLog.DiagnosticLocation;
        AddEvent(
            "Startup-safe window initialized. No transport has been created or enumerated.");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _startupLog.Stage(
            "MainWindow Loaded event completed without transport activity.");
        AddEvent("Choose a manual action to create an inspection transport.");
    }

    private async void RunFakeEnumerationButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunEnumerationAsync(
            "Fake enumeration",
            _inspectionController.RunFakeEnumerationAsync,
            false);

    private async void InspectD2xxDevicesButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunEnumerationAsync(
            "D2XX inspection",
            _inspectionController.InspectD2xxDevicesAsync,
            true);

    private async void OpenExactDeviceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_inspectionController.CurrentTransport is not
            D2xxInspectionTransport d2xx)
        {
            AddEvent("Inspect D2XX Devices before opening.");
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "Close the original MyPlasm software. Keep 48 V motor power and " +
            "plasma power off, and disconnect torch start. This opens only " +
            "the exact enumerated serial for passive receive and performs zero writes.",
            "Passive receive safety confirmation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            AddEvent("Passive device open cancelled by the operator.");
            return;
        }

        try
        {
            _passiveSession = d2xx.CreatePassiveSession();
            await _passiveSession.OpenAsync();
            _captureService = new PassiveCaptureService(_passiveSession);
            SelectedDeviceText.Text =
                _passiveSession.SelectedDevice.Description;
            SerialText.Text = _passiveSession.SelectedDevice.SerialNumber;
            DriverVersionText.Text =
                _passiveSession.DriverVersion ?? "Unavailable";
            AddEvent(
                "Exact MyPlasm device opened by its enumerated serial only; transmit count remains zero.");
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Open exact device", exception);
            AddEvent(exception.Message);
            if (_passiveSession is not null)
            {
                await _passiveSession.DisposeAsync();
                _passiveSession = null;
            }

            _captureService = null;
        }
        finally
        {
            UpdateControlState();
        }
    }

    private async void StartCaptureButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureService is null || _passiveSession?.IsOpen != true)
        {
            AddEvent("Open the exact device first.");
            return;
        }

        if (!int.TryParse(CaptureDurationTextBox.Text, out int seconds) ||
            seconds <= 0 ||
            seconds > PassiveCaptureService.MaximumDuration.TotalSeconds)
        {
            AddEvent(
                "Capture duration must be a whole number from 1 through 300 seconds.");
            return;
        }

        try
        {
            CaptureStateText.Text = "Running";
            UpdateControlState();
            Progress<PassiveCaptureProgress> progress = new(UpdateProgress);
            PassiveCaptureResult capture = await _captureService.StartAsync(
                TimeSpan.FromSeconds(seconds),
                progress);
            _lastCapture = capture;
            CaptureStateText.Text = $"Stopped ({capture.StopReason})";
            InspectionStatusText.Text =
                $"Passive capture stopped: {capture.TotalBytes} bytes; " +
                $"{capture.Chunks.Count} chunks; transmit count: 0";
            AddEvent(
                $"Passive receive capture completed ({capture.StopReason}); zero transmits.");
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Passive capture", exception);
            AddEvent(exception.Message);
        }
        finally
        {
            UpdateControlState();
        }
    }

    private async void StopCaptureButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureService is null)
        {
            return;
        }

        try
        {
            StopCaptureButton.IsEnabled = false;
            PassiveCaptureResult? capture =
                await _captureService.StopAsync();
            if (capture is not null)
            {
                _lastCapture = capture;
                CaptureStateText.Text =
                    $"Stopped ({capture.StopReason})";
                AddEvent(
                    "Stop Capture cancelled and awaited the passive capture.");
            }
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Stop passive capture", exception);
            AddEvent(exception.Message);
        }
        finally
        {
            UpdateControlState();
        }
    }

    private async void CloseDeviceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await CloseDeviceAsync();
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Close passive device", exception);
            AddEvent(exception.Message);
        }
        finally
        {
            UpdateControlState();
        }
    }

    private void ExportDiagnosticPackageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_lastCapture is null ||
            _inspectionController.CurrentTransport is not
                D2xxInspectionTransport d2xx)
        {
            AddEvent("Run and stop a passive capture before export.");
            return;
        }

        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "MyPlasm Inspector",
                "Captures");
            CaptureExportContext context = new(
                "MyPlasm Inspector",
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
                    "Unknown",
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                _softwareRenderingActive ? "Software" : "Hardware",
                D2xxInspectionTransport.DefaultRelativeLibraryPath,
                d2xx.LibraryInspection,
                d2xx.LibraryVersion,
                _startupLog.IsPersistentFileLoggingAvailable
                    ? _startupLog.FilePath
                    : null);
            CaptureExportResult exported =
                new CaptureExporter().Export(_lastCapture, context, root);
            AddEvent(
                $"Capture ZIP exported and hash-verified: {exported.ZipPath}; " +
                $"SHA-256 {exported.ZipSha256}");
        }
        catch (Exception exception)
        {
            _startupLog.Exception("Capture export", exception);
            AddEvent(exception.Message);
        }
    }

    private async Task CloseDeviceAsync()
    {
        if (_captureService is null)
        {
            return;
        }

        D2xxStatus? closeStatus =
            await _captureService.CloseSessionAsync();
        _lastCapture = _captureService.LastCapture ?? _lastCapture;
        LastStatusText.Text = closeStatus?.ToString() ?? "No handle";
        if (_passiveSession?.HasUnresolvedCloseFailure == true)
        {
            DeviceStateText.Text =
                "CLOSE UNCONFIRMED — restart required";
            AddEvent(
                $"FT_Close did not report success ({closeStatus}). " +
                "No reopen or new enumeration is permitted in this process.");
            return;
        }

        DeviceStateText.Text = "Closed";
        AddEvent(
            "Capture stopped and awaited; passive device session closed.");
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
            await DisposeCompletedSessionBeforeEnumerationAsync();
            IReadOnlyList<FtdiDeviceInfo> devices =
                await action(CancellationToken.None);
            FtdiDeviceInfo[] candidates = devices
                .Where(device => device.IsMyPlasmController)
                .ToArray();
            InspectionStatusText.Text =
                $"{actionName}: {devices.Count} device(s); " +
                $"{candidates.Length} exact MyPlasm candidate(s).";
            AddEvent(
                $"{actionName} completed without opening a device or transmitting bytes.");

            if (candidates.Length == 1)
            {
                SelectedDeviceText.Text = candidates[0].Description;
                SerialText.Text =
                    string.IsNullOrWhiteSpace(candidates[0].SerialNumber)
                        ? "(missing)"
                        : candidates[0].SerialNumber;
            }

            if (d2xxInspection &&
                _inspectionController.CurrentTransport is
                    D2xxInspectionTransport d2xx)
            {
                foreach (D2xxDiagnostic diagnostic in d2xx.Diagnostics)
                {
                    AddEvent(
                        $"{diagnostic.Severity}: {diagnostic.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            InspectionStatusText.Text =
                $"{actionName} failed. See startup diagnostics.";
            _startupLog.Exception(actionName, exception);
            AddEvent($"{actionName} error: {exception.Message}");
        }
        finally
        {
            UpdateControlState();
        }
    }

    private async Task DisposeCompletedSessionBeforeEnumerationAsync()
    {
        if (_passiveSession?.HasUnresolvedCloseFailure == true)
        {
            throw new InvalidOperationException(
                "Native close is unresolved; new enumeration is refused until the process exits.");
        }

        if (_captureService is not null)
        {
            await _captureService.DisposeAsync();
        }

        _captureService = null;
        _passiveSession = null;
        _lastCapture = null;
    }

    private void UpdateProgress(PassiveCaptureProgress progress)
    {
        ElapsedText.Text = progress.Elapsed.ToString(@"hh\:mm\:ss");
        BytesReceivedText.Text = progress.TotalBytes.ToString();
        ChunksReceivedText.Text = progress.ChunkCount.ToString();
        QueueDepthText.Text = progress.QueueDepth.ToString();
        LastStatusText.Text = progress.LastStatus.ToString();
    }

    private void UpdateControlState()
    {
        bool open = _passiveSession?.IsOpen == true;
        bool closeUnresolved =
            _passiveSession?.HasUnresolvedCloseFailure == true;
        bool capturing = _captureService?.IsCapturing == true;
        bool exactCandidate =
            _inspectionController.CurrentTransport is
                D2xxInspectionTransport d2xx &&
            d2xx.CanCreatePassiveSession;

        DeviceStateText.Text = closeUnresolved
            ? "CLOSE UNCONFIRMED — restart required"
            : open
                ? "Open"
                : "Closed";
        RunFakeEnumerationButton.IsEnabled =
            !open && !capturing && !closeUnresolved;
        InspectD2xxDevicesButton.IsEnabled =
            !open && !capturing && !closeUnresolved;
        OpenExactDeviceButton.IsEnabled =
            exactCandidate && !open && !capturing && !closeUnresolved;
        StartCaptureButton.IsEnabled = open && !capturing;
        StopCaptureButton.IsEnabled = capturing;
        CloseDeviceButton.IsEnabled = open && !capturing;
        CaptureDurationTextBox.IsEnabled = !capturing;
        ExportDiagnosticPackageButton.IsEnabled =
            _lastCapture is not null &&
            !capturing &&
            (!open || closeUnresolved);
    }

    private void AddEvent(string message)
    {
        EventLog.Items.Insert(
            0,
            $"{DateTimeOffset.Now:T}  {message}");
        while (EventLog.Items.Count > MaximumUiEvents)
        {
            EventLog.Items.RemoveAt(EventLog.Items.Count - 1);
        }

        _startupLog.Stage($"UI: {message}");
    }

    private async void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_closeReady)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            _startupLog.Stage(
                "MainWindow closing; cancelling and awaiting capture before device close.");
            if (_captureService is not null)
            {
                await _captureService.DisposeAsync();
            }

            await _inspectionController.DisposeAsync();
        }
        catch (Exception exception)
        {
            _startupLog.Exception("MainWindow closing", exception);
        }
        finally
        {
            _closeReady = true;
            Closing -= Window_Closing;

            // WPF keeps the window in its internal closing state until this
            // event handler returns. Queue the final close so that even
            // synchronously completed cleanup cannot re-enter Close().
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(Close));
        }
    }
}
