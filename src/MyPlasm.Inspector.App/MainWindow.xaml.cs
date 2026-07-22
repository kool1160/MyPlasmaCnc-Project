using System.Collections.ObjectModel;
using System.Windows;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.App;

public partial class MainWindow : Window
{
    private readonly FakeFtdiTransport _transport = new();
    private readonly ObservableCollection<FtdiDeviceInfo> _devices = [];

    public MainWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
        AddEvent("Foundation shell started in fake-transport mode.");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
        AddEvent("Simulated FTDI device list refreshed without opening a device.");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
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
        IReadOnlyList<FtdiDeviceInfo> devices = await _transport.EnumerateDevicesAsync();
        _devices.Clear();

        foreach (FtdiDeviceInfo device in devices)
        {
            _devices.Add(device);
        }

        DevicesGrid.SelectedItem = _devices.FirstOrDefault(device => device.IsMyPlasmController);
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
