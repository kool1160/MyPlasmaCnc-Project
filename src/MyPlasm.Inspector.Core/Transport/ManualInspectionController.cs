namespace MyPlasm.Inspector.Core.Transport;

/// <summary>
/// Creates an inspection transport only after an operator requests enumeration.
/// It intentionally exposes no open, read, or write operation.
/// </summary>
public sealed class ManualInspectionController : IAsyncDisposable
{
    private readonly Func<IControllerTransport> _fakeTransportFactory;
    private readonly Func<IControllerTransport> _d2xxTransportFactory;
    private IControllerTransport? _currentTransport;

    public ManualInspectionController(
        Func<IControllerTransport> fakeTransportFactory,
        Func<IControllerTransport> d2xxTransportFactory)
    {
        _fakeTransportFactory = fakeTransportFactory ?? throw new ArgumentNullException(nameof(fakeTransportFactory));
        _d2xxTransportFactory = d2xxTransportFactory ?? throw new ArgumentNullException(nameof(d2xxTransportFactory));
    }

    public IControllerTransport? CurrentTransport => _currentTransport;

    public async ValueTask<IReadOnlyList<FtdiDeviceInfo>> RunFakeEnumerationAsync(
        CancellationToken cancellationToken = default) =>
        await CreateAndEnumerateAsync(_fakeTransportFactory, cancellationToken);

    public async ValueTask<IReadOnlyList<FtdiDeviceInfo>> InspectD2xxDevicesAsync(
        CancellationToken cancellationToken = default) =>
        await CreateAndEnumerateAsync(_d2xxTransportFactory, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_currentTransport is null)
        {
            return;
        }

        IControllerTransport transport = _currentTransport;
        _currentTransport = null;
        await transport.DisposeAsync();
    }

    private async ValueTask<IReadOnlyList<FtdiDeviceInfo>> CreateAndEnumerateAsync(
        Func<IControllerTransport> transportFactory,
        CancellationToken cancellationToken)
    {
        await DisposeAsync();
        _currentTransport = transportFactory() ?? throw new InvalidOperationException("Transport factory returned null.");
        return await _currentTransport.EnumerateDevicesAsync(cancellationToken);
    }
}
