using Microsoft.Win32.SafeHandles;

namespace MyPlasm.Inspector.Transport.D2xx;

internal sealed class D2xxSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly ID2xxNativeApi _nativeApi;
    private bool _closeAttempted;

    public D2xxSafeHandle(ID2xxNativeApi nativeApi, nint handle)
        : base(true)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        SetHandle(handle);
    }

    public D2xxStatus? CloseStatus { get; private set; }

    public bool TryClose(out D2xxStatus status)
    {
        if (_closeAttempted)
        {
            status = CloseStatus ?? D2xxStatus.InvalidHandle;
            return status == D2xxStatus.Ok;
        }

        _closeAttempted = true;
        status = _nativeApi.Close(handle);
        CloseStatus = status;
        if (status == D2xxStatus.Ok)
        {
            SetHandleAsInvalid();
        }

        return status == D2xxStatus.Ok;
    }

    protected override bool ReleaseHandle()
    {
        if (!_closeAttempted)
        {
            _closeAttempted = true;
            CloseStatus = _nativeApi.Close(handle);
        }

        return CloseStatus == D2xxStatus.Ok;
    }
}
