namespace MyPlasm.Inspector.Transport.D2xx;

public enum D2xxStatus : uint
{
    Ok = 0,
    InvalidHandle = 1,
    DeviceNotFound = 2,
    DeviceNotOpened = 3,
    IoError = 4,
    InsufficientResources = 5,
    InvalidParameter = 6,
    InvalidBaudRate = 7,
    DeviceNotOpenedForErase = 8,
    DeviceNotOpenedForWrite = 9,
    FailedToWriteDevice = 10,
    EepromReadFailed = 11,
    EepromWriteFailed = 12,
    EepromEraseFailed = 13,
    EepromNotPresent = 14,
    EepromNotProgrammed = 15,
    InvalidArguments = 16,
    NotSupported = 17,
    OtherError = 18
}
