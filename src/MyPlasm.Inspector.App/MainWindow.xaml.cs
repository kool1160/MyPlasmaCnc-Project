using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.D2xx;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FtdiDeviceInfo> _devices = [];
    private IControllerTransport _transport = new FakeFtdiTransport();

    public MainWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
        AddEvent("Enumeration-only shell started in fake-transport mode.");
    }

    private async void TransportSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TransportSelector.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        await _transport.DisposeAsync();
        bool useD2xx = string.Equals(selected.Tag?.ToString(), "D2xx", StringComparison.Ordinal);
        _transport = useD2xx
            ? D2xxInspectionTransport.CreateDefault()
            : new FakeFtdiTransport();

        ConnectButton.IsEnabled = !useD2xx;
        DisconnectButton.IsEnabled = !useD2xx;
        DeviceListHeading.Text = useD2xx ? "FTDI devices (D2XX)" : "FTDI devices (simulated)";
        ConnectionStatusText.Text = "Enumeration idle";
        LibraryStatusText.Text = useD2xx ? "Waiting for D2XX inspection" : "Not loaded in fake mode";
        _devices.Clear();

        AddEvent(useD2xx
            ? "D2XX inspection mode selected. Device open/read/write operations are unavailable."
            : "Fake transport selected.");
        await RefreshDevicesAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transport is not FakeFtdiTransport)
        {
            AddEvent("Open refused: D2XX mode is device-enumeration-only.");
            return;
        }

        if (DevicesGrid.SelectedItem is not FtdiDeviceInfo selected)
        {
            AddEvent("Select the simulated MyPlasm device first.");
            return;
        }

        if (!selected.IsMyPlasmController)
        {
            AddEvent("Connection refused: unrelated FTDI devices are never opened automatically.");
            return;
        }

        try
        {
            await _transport.OpenAsync(selected.SerialNumber);
            ConnectionStatusText.Text = $"Connected to fake {selected.SerialNumber}";
            AddEvent($"Opened simulated target {selected.SerialNumber}; no bytes transmitted.");
            await RefreshDevicesAsync();
        }
        catch (InvalidOperationException exception)
        {
            AddEvent(exception.Message);
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _transport.CloseAsync();
        ConnectionStatusText.Text = "Disconnected";
        AddEvent("Simulated target closed; no bytes transmitted.");
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            IReadOnlyList<FtdiDeviceInfo> devices = await _transport.EnumerateDevicesAsync();
            _devices.Clear();

            foreach (FtdiDeviceInfo device in devices)
            {
                _devices.Add(device);
            }

            DevicesGrid.SelectedItem = _devices.FirstOrDefault(device => device.IsMyPlasmController);
            int candidates = _devices.Count(device => device.IsMyPlasmController);
            ConnectionStatusText.Text = $"{_devices.Count} device(s); {candidates} exact candidate(s)";

            if (_transport is D2xxInspectionTransport d2xx)
            {
                LibraryStatusText.Text = BuildLibraryStatus(d2xx);
                foreach (D2xxDiagnostic diagnostic in d2xx.Diagnostics)
                {
                    AddEvent($"{diagnostic.Severity}: {diagnostic.Message}");
                }

                AddEvent("D2XX enumeration completed without opening a device or transmitting bytes.");
            }
            else
            {
                AddEvent("Simulated FTDI enumeration completed without opening a device.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            ConnectionStatusText.Text = "Enumeration failed";
            AddEvent($"Enumeration error: {exception.Message}");
        }
    }

    private static string BuildLibraryStatus(D2xxInspectionTransport transport)
    {
        if (transport.LibraryInspection is PeInspectionResult inspection)
        {
            string version = transport.LibraryVersion ?? inspection.FileVersion ?? "version unavailable";
            return $"{inspection.DllArchitecture}; {version}; SHA-256 {inspection.Sha256[..12]}…";
        }

        return transport.LibraryVersion is null
            ? "D2XX unavailable — see diagnostics"
            : $"D2XX {transport.LibraryVersion}";
    }

    private void AddEvent(string message)
    {
        EventLog.Items.Insert(0, $"{DateTimeOffset.Now:T}  {message}");
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        await _transport.DisposeAsync();
    }
}
