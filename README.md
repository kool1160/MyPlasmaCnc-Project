# MyPlasm Inspector

A small Windows diagnostic application for Chris Hilton's existing Proma MyPlasm CNC controller.

## Mission

Build a safe, useful Windows application that can discover the controller through FTDI D2XX, connect to it, collect every non-destructive piece of information the board exposes, record raw traffic, decode confirmed status data, and export a complete diagnostic package.

This project does **not** replace the controller hardware. The existing MyPlasm board, plasma interface, motor drives, sensors, floating-head touch-off, and plasma source remain in place.

## Three-stage plan

1. **Connect and inspect** — enumerate FTDI devices, identify MyPlasm, connect safely, capture metadata and passive traffic.
2. **Decode useful information** — firmware version, coordinates, reference state, inputs, plasma-interface status, and unknown packets where evidence supports decoding.
3. **Polish and package** — clean Windows UI, evidence export, tests, portable build, installer, and documentation.

## Safety boundary

Version 1 is read-only. It must not jog, home, alter settings, toggle outputs, start plasma, write FTDI EEPROM, or upload firmware. Any transmitted bytes must pass a narrow, evidence-backed allowlist. Unknown commands are rejected before reaching the FTDI transport.

Initial live validation must be performed with the 48 V motor supply and plasma source disabled.

## Repository layout

- `src/` — application source
- `tests/` — automated tests and fake transport fixtures
- `docs/` — protocol notes, evidence rules, and operator instructions
- `evidence/` — small sanitized samples only; large vendor/runtime archives remain outside normal Git history
- `Old installed software/` — historical evidence from the original installation; treat as read-only reference material

## Technology direction

- .NET 8 Windows desktop application
- FTDI D2XX native interop
- Explicit architecture handling for the supplied 32-bit DLL
- Separate UI, transport, protocol, safety-policy, and evidence-export layers
- Fake transport for deterministic tests
- Offline operation

## First work item

See GitHub Issue #1: **Project 1: Build read-only MyPlasm Inspector for Windows**.

## Ground rule

Confirmed facts, hypotheses, and unknowns must be labeled separately. No controller command is considered safe merely because it appears plausible.
