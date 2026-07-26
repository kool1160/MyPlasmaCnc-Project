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

- `D2xxInspectionTransport` keeps enumeration handle-free through
  `FT_CreateDeviceInfoList`, `FT_GetDeviceInfoList`, and
  `FT_GetLibraryVersion`.
- Its application-facing `IControllerTransport` open, read, and write methods
  remain non-functional and throw `NotSupportedException`.
- A separate operator-confirmed session can use only `FT_OpenEx` with the exact
  enumerated serial, `FT_GetDriverVersion`, `FT_GetQueueStatus`, `FT_Read`, and
  `FT_Close`.
- Native injection is internal to the transport assembly and its test friend;
  application callers cannot supply an arbitrary native API or serial.
- Missing DLL, PE architecture mismatch, load failure, driver/device absence, and duplicate identifiers produce diagnostics without opening a device.
- The production native loader centralizes exactly eight required passive
  export names. Automated tests reject any compiled `FT_Write`, configuration,
  bit-mode, baud-rate, reset, purge, EEPROM, or firmware export reference.
- Inconsistent native device counts fail closed without returning a partial
  device list, and a disposed inspection transport cannot reload or enumerate.
- The production command allowlist remains empty.

## Portable package safety status

- The self-contained `win-x86` package includes an inspected local FTDI DLL
  but remains subject to the same empty production command allowlist.
- Its D2XX inspection mode remains handle-free. A separately confirmed passive
  session can open the unique exact candidate and read queued bytes, but it has
  no controller-write, EEPROM, configuration, reset, purge, or firmware path.
- The launcher only verifies package files and starts the application; it does
  not request elevation or communicate with a controller.
- The packaged `README-FIRST.txt` repeats the required first-live-validation
  power isolation: 24 V controller power only, with motor power, plasma source,
  and torch-start circuit disabled.

## Startup-safe diagnostic package

- WPF software rendering is forced before `MainWindow` construction unless the
  operator explicitly passes `--hardware-rendering` for comparison.
- The first window creates no fake or D2XX transport, inspects no DLL,
  enumerates no devices, and cannot reach a controller open, read, or write
  operation.
- Fake enumeration and D2XX metadata inspection each require a separate manual
  button click. D2XX native loading cannot begin before that click.
- Startup logging is non-throwing from the application's perspective.
  Directory, append, locking, permissions, disk, environment-probe, DLL
  presence, and DLL hashing failures cannot block startup or exception
  handling.
- When persistent logging is available, startup exceptions are written with
  stack traces to `%LOCALAPPDATA%\MyPlasm Inspector\Logs\`.
- The first persistent logging failure disables further file writes for that
  logger instance. Later entries remain in a bounded in-memory buffer and
  best-effort Trace output without recursive retry.
- The first window clearly reports when startup file logging is unavailable.

## Transactional portable-package status

- The package builder accepts only the confirmed x86 D2XX DLL with file version
  `3.01.19`, size `206144` bytes, and the documented SHA-256.
- Packaging requires a clean Git worktree and records the exact source commit,
  application hash, and D2XX evidence in `package-manifest.json`.
- Publish, template copy, PE checks, evidence checks, manifest creation, and ZIP
  creation occur in a unique staging directory.
- The staged ZIP is reopened and every required entry, safe relative path,
  application hash, DLL hash/size, and manifest field is validated before the
  final package changes.
- A prior package directory and ZIP are validated as a pair. Ambiguous states
  are refused. Publication failures restore the prior pair and preserve failed
  replacement evidence in quarantine.

## Passive receive safety status

- Startup, fake enumeration, and D2XX enumeration still create no device
  handle automatically.
- Opening requires exactly one exact `MyPlasm CNC` description, a nonempty
  serial, a present nonzero location, no duplicate serial or location, a
  not-already-open enumeration result, an explicit operator confirmation, and
  no running `MyPlasmCNC` process.
- Only the enumerated serial reaches `FT_OpenEx`; there is no arbitrary serial
  input and no public native-injection surface.
- A nonzero handle returned with a failed `FT_OpenEx` status, or assigned before
  `FT_OpenEx` throws, is treated as unexpectedly live and closed exactly once
  through the same cleanup policy. Failed cleanup is recorded as unresolved
  and blocks all later open or enumeration work in that process.
- Capture is limited to five minutes, 64 MiB of retained bytes, 100,000
  capture events, and 16,384 receive chunks. Elapsed duration uses a monotonic
  clock.
- Queue/read counts are validated before bytes are retained. Zero queue depth
  never calls read. Native exceptions and invalid counts stop the capture while
  preserving prior evidence.
- Stop, close, and window close cancel and await capture before native close.
  Close is attempted exactly once and cannot be suppressed by cancellation.
- A failed or exceptional native close is recorded as unresolved. No later
  open or enumeration is allowed in that process.
- Raw bytes and events are streamed to unique export files. Event sequence
  numbers are allocated under the event lock and are unique and contiguous.
  JSONL contains one compact object per line. `session.json` and `report.txt`
  include event count, first and last sequence, and explicit unique, monotonic,
  and contiguous results. The exact packaged source commit is recorded in
  `session.json`, `report.txt`, and `source-commit.txt`, which is covered by
  `hashes.sha256`. A staged ZIP is reopened and every entry hash is compared
  with its source before the final ZIP name is published.
- Export failure preserves the unique raw evidence directory. Local D2XX source
  paths are not written to structured metadata.
- Transmit count and production allowlist count remain fixed at zero.

## Offline three-run comparison safety status

- The comparison command accepts exactly three manifest-verified directories
  containing only the analyzer's six sanitized outputs.
- Raw captures, binaries, archives, payload files, duplicate inputs, and
  normalized or link-resolved input/output collisions fail closed.
- Comparison has no D2XX, hardware, native-library, transport, replay, or
  command-generation path and never modifies an input report set.
- `Stable across all three captures` means only exact sanitized fingerprint
  presence in all three inputs. It does not mean safe, meaningful, causal, or
  replayable.
- Live collection remains operator-controlled under
  `docs/differential-capture-campaign.md`; it is not performed by CI or the
  comparison tool.
