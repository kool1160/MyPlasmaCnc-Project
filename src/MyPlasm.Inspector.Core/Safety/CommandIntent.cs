namespace MyPlasm.Inspector.Core.Safety;

public enum CommandIntent
{
    Unknown = 0,
    ReadOnlyIdentification,
    ReadOnlyStatus,
    Motion,
    HomingOrProbing,
    OutputControl,
    PlasmaStart,
    ConfigurationWrite,
    FirmwareOperation,
    FtdiEepromWrite
}
