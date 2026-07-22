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
