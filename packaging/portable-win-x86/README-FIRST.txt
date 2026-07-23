MYPLASM INSPECTOR - PORTABLE WINDOWS PACKAGE
=============================================

1. Copy MyPlasmInspector-win-x86-diagnostic.zip to the plasma-table computer.
2. Extract the ENTIRE ZIP to a normal writable folder, for example Desktop\MyPlasmInspector.
3. Double-click "Launch MyPlasm Inspector.bat".

If the app exits unexpectedly, use "Launch MyPlasm Inspector Diagnostic.bat".
It writes launcher.log in the extracted package, reports the application exit
code, opens the startup-log folder, and keeps its message window open.

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
The first window deliberately creates no transport and performs no enumeration.
Use "Run Fake Enumeration" for an offline demonstration or "Inspect D2XX
Devices" to explicitly start metadata-only D2XX inspection. Software rendering
is the safe default. The optional --hardware-rendering command-line argument is
only for comparison during troubleshooting.

Startup logs are written to:
%LOCALAPPDATA%\MyPlasm Inspector\Logs\

If the launcher reports a missing file, extract the full ZIP again; do not run
the application from inside the ZIP viewer.
