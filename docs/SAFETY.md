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

- `D2xxInspectionTransport` performs DLL inspection, enumeration, library
  metadata, and exact-candidate discovery without opening a device.
- Its application-facing `IControllerTransport` open, read, and write methods
  remain non-functional. Operator-confirmed passive work uses the separate
  `PassiveD2xxSession`.
- Driver version is queried only after a successful explicit session open.
- Missing DLL, PE architecture mismatch, load failure, driver/device absence,
  and duplicate identifiers produce diagnostics without opening a device.
- The production command allowlist remains empty.

## Portable package safety status

- The self-contained `win-x86` package includes an inspected local FTDI DLL but
  remains subject to the same empty production command allowlist.
- Its D2XX inspection mode does not open automatically. A separate confirmed
  passive session can open the unique exact serial and read queued receive
  bytes, but cannot transmit, alter EEPROM, change communication configuration,
  reset, purge, or update firmware.
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

## Passive receive implementation status

- D2XX enumeration remains handle-free and cannot automatically open a device.
- Opening requires a separate operator confirmation, exactly one exact
  `MyPlasm CNC` description match, a nonempty unique serial number, a unique
  location, a not-already-open enumeration result, and confirmation that the
  original MyPlasm process is not running.
- The exact serial returned by enumeration is the only value passed to native
  open. The application exposes no arbitrary serial input.
- A dedicated safe handle records native close status. A failed close remains
  visible as a failure and is not reported as successfully closed.
- Passive capture uses only queue-status polling and receive reads. Every queue
  poll, including zero depth, and every open, metadata, read, cancellation,
  disconnect, error, and close operation is timestamped in the session evidence.
- Capture work runs outside the WPF thread. Stop, device close, and window close
  cancel and await active capture work before native close.
- Receive counts are validated against both the request and buffer before bytes
  are copied. Invalid counts stop safely and preserve earlier bytes.
- Zero-byte captures are valid and exportable.
- CI scans production source for prohibited native transmit, EEPROM,
  communication-configuration, reset, purge, and firmware entry points.
- The production command allowlist and transmit count remain fixed at zero.
