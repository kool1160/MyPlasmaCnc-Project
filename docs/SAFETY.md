# Read-Only Safety Policy

## Initial hardware state

For first live validation:

- MyPlasm controller powered by its 24 V supply;
- USB connected to the Windows PC;
- 48 V motor-drive supply disabled;
- Everlast plasma source disabled;
- torch-start circuit disabled or disconnected;
- firmware update functions unavailable.

## Software enforcement

All controller writes must pass through one centralized command policy.

The default policy is deny-all. A command may be allowed only when:

1. its exact bytes and purpose are documented;
2. evidence establishes it as non-destructive and read-only;
3. tests cover the allowed command;
4. tests prove unsafe and unknown commands are rejected before transport;
5. the command is reviewed separately from UI code.

## Permanently blocked in Version 1

- jog and continuous motion;
- homing and probing;
- torch or auxiliary outputs;
- controller configuration writes;
- FTDI EEPROM writes;
- firmware upload, erase, reset-to-bootloader, or update;
- arbitrary raw-command entry;
- replay of unclassified captures.

## Capture requirements

Every transmitted and received byte must be recorded with:

- UTC timestamp;
- direction;
- exact payload;
- session identifier;
- decoder result, when known;
- classification: confirmed, hypothesis, or unknown.

Raw evidence must never be silently normalized or rewritten.

## Failure behavior

The application fails closed:

- native-library mismatch prevents connection;
- unknown device identity prevents automatic opening;
- unknown command prevents transmission;
- decoder failure preserves raw bytes and reports unknown data;
- export failure does not delete the original session capture.

## Foundation implementation status

- The production command allowlist is empty.
- `SafeControllerSession` is the sole application command gateway and checks the centralized policy before creating a transport-valid command.
- Transport writes accept only a validated command object whose constructor is internal to the core assembly.
- The initial Windows shell uses `FakeFtdiTransport`; it contains no native D2XX binding and no command-send UI.
- Automated tests use synthetic sentinel bytes, not inferred controller commands, to prove motion, homing/probing, output, plasma, configuration, firmware, EEPROM, unknown, and unverified read-only intents are blocked before transport.

## D2XX enumeration implementation status

- `D2xxInspectionTransport` exposes enumeration through only `FT_CreateDeviceInfoList`, `FT_GetDeviceInfoList`, and `FT_GetLibraryVersion`.
- Its injectable native API has no open, read, write, configuration, bit-mode, baud-rate, or EEPROM functions.
- The D2XX transport's application-facing open, read, and write methods are non-functional and throw `NotSupportedException`.
- Driver version remains unqueried because FTDI documents `FT_GetDriverVersion` as requiring an open device handle.
- Missing DLL, PE architecture mismatch, load failure, driver/device absence, and duplicate identifiers produce diagnostics without opening a device.
- The production command allowlist remains empty.

## Portable package safety status

- The self-contained `win-x86` package includes an inspected local FTDI DLL but
  remains subject to the same empty production command allowlist.
- Its D2XX inspection mode is device enumeration only: it has no controller-open,
  read, write, EEPROM, baud-rate, or bit-mode path.
- The launcher only verifies package files and starts the application; it does not
  request elevation or communicate with a controller.
- The packaged `README-FIRST.txt` repeats the required first-live-validation power
  isolation: 24 V controller power only, with motor power, plasma source, and
  torch-start circuit disabled.

## Startup-safe diagnostic package

- WPF software rendering is forced before `MainWindow` construction unless the
  operator explicitly passes `--hardware-rendering` for comparison.
- The first window creates no fake or D2XX transport, inspects no DLL, enumerates
  no devices, and cannot reach a controller open, read, or write operation.
- Fake enumeration and D2XX metadata inspection each require a separate manual
  button click. D2XX native loading cannot begin before that click.
- Startup exceptions are written with stack traces to
  `%LOCALAPPDATA%\MyPlasm Inspector\Logs\`.
