MYPLASM INSPECTOR - PORTABLE WINDOWS PACKAGE
=============================================

1. Copy MyPlasmInspector-win-x86.zip to the plasma-table computer.
2. Extract the ENTIRE ZIP to a normal writable folder, for example Desktop\MyPlasmInspector.
3. Double-click "Launch MyPlasm Inspector.bat".

No .NET runtime or SDK is needed on the plasma-table computer. This is a
self-contained 32-bit Windows application.

Before the first live check:
- Keep the MyPlasm controller on its 24 V supply only.
- Keep the 48 V motor-drive supply disabled.
- Keep the plasma source disabled and the torch-start circuit disconnected.
- Connect the controller USB cable only after the above conditions are met.

If Windows says the FTDI driver is missing, install the manufacturer-provided
FTDI driver for the connected controller. Driver installation may require an
administrator account; starting this package normally does not.

SAFETY
------
This build is DEVICE ENUMERATION ONLY — NO CONTROLLER COMMANDS.
It can list FTDI metadata and identify a device whose description exactly
matches "MyPlasm CNC". It cannot open a controller, read controller traffic,
send controller bytes, move axes, change outputs, alter EEPROM, change baud
rate or bit mode, or update firmware.

In the app, choose "D2XX inspection transport" and then enumerate devices.
The Fake transport is available for an offline demonstration.

If the launcher reports a missing file, extract the full ZIP again; do not run
the application from inside the ZIP viewer.
