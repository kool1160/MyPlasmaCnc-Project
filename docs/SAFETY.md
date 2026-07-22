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
