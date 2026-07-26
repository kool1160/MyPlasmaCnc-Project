using Microsoft.Win32.SafeHandles;

namespace MyPlasm.Inspector.Transport.D2xx;

internal sealed class D2xxSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly object _closeGate = new();
    private readonly ID2xxNativeApi _nativeApi;
    private bool _closeAttempted;

    public D2xxSafeHandle(ID2xxNativeApi nativeApi, nint handle)
        : base(true)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        SetHandle(handle);
    }

    public D2xxStatus? CloseStatus { get; private set; }

    public string? CloseFailure { get; private set; }

    public bool TryClose(out D2xxStatus status)
    {
        lock (_closeGate)
        {
            status = CloseOnceNoThrow();
            if (status == D2xxStatus.Ok && !IsInvalid)
            {
                SetHandleAsInvalid();
            }

            return status == D2xxStatus.Ok;
        }
    }

    protected override bool ReleaseHandle()
    {
        lock (_closeGate)
        {
            return CloseOnceNoThrow() == D2xxStatus.Ok;
        }
    }

    private D2xxStatus CloseOnceNoThrow()
    {
        if (_closeAttempted)
        {
            return CloseStatus ?? D2xxStatus.InvalidHandle;
        }

        _closeAttempted = true;
        try
        {
            CloseStatus = _nativeApi.Close(handle);
        }
        catch (Exception exception)
        {
            CloseStatus = D2xxStatus.OtherError;
            CloseFailure = $"{exception.GetType().Name}: {exception.Message}";
        }

        return CloseStatus.Value;
    }
}
