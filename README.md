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

## Current foundation

The completed Issue #1 foundation slices contain:

- `MyPlasm.Inspector.Core` — transport contracts and the centralized command safety boundary;
- `MyPlasm.Inspector.Transport.Fake` — deterministic, hardware-free FTDI behavior;
- `MyPlasm.Inspector.Transport.D2xx` — enumeration-only production D2XX interop;
- `MyPlasm.Inspector.App` — a WPF shell with explicit fake and D2XX inspection modes;
- `MyPlasm.Inspector.Tests` — offline fake-transport and safety-policy tests.
- `MyPlasm.Inspector.PeInspector` — local vendor-DLL architecture, version, and hash inspection.

The production command allowlist remains empty because no controller request bytes are confirmed. D2XX mode can list device metadata but cannot open, read, configure, or write a device. Driver version is deliberately not queried because FTDI documents that operation as requiring an open device handle.

### Build and test

Install a .NET 8 SDK, then run from the repository root:

```powershell
dotnet restore MyPlasm.Inspector.sln
dotnet build MyPlasm.Inspector.sln --configuration Release --no-restore
dotnet test MyPlasm.Inspector.sln --configuration Release --no-build
```

Launch the Windows shell with:

```powershell
dotnet run --project src/MyPlasm.Inspector.App/MyPlasm.Inspector.App.csproj
```

### Local vendor DLL

The historical evidence in Git does not contain `ftd2xx.dll`. Obtain the DLL separately and place it at `native/local/ftd2xx.dll`; that directory and filename are ignored by Git. Do not place vendor binaries under `Old installed software/` or force-add them.

Inspect a local DLL before running D2XX mode:

```powershell
dotnet run --project tools/MyPlasm.Inspector.PeInspector -- native/local/ftd2xx.dll
```

Use `--architecture x86` or `--architecture x64` to check a chosen application architecture. See `native/README.md` for the complete local-only setup.

### Portable Windows package

With an inspected, legally obtained x86 `ftd2xx.dll` staged at
`native/local/ftd2xx.dll`, double-click `Build Portable Inspector.bat`. It creates:

```text
artifacts/MyPlasmInspector-win-x86-diagnostic.zip
```

The ZIP is a self-contained .NET 8 `win-x86` package. Copy it to the
plasma-table PC, extract the complete archive, and double-click
`Launch MyPlasm Inspector.bat`. No .NET SDK or runtime is required on that target
PC. The FTDI driver is still required by Windows and may need administrator rights
to install; launching the package does not. The first window uses software rendering
and creates no transport until an operator clicks a manual enumeration button. If it
exits unexpectedly, use `Launch MyPlasm Inspector Diagnostic.bat`; it writes
`launcher.log` beside the app and opens `%LOCALAPPDATA%\MyPlasm Inspector\Logs`.
See `native/README.md` and the packaged `README-FIRST.txt` for the safety setup and
full instructions.

## Ground rule

Confirmed facts, hypotheses, and unknowns must be labeled separately. No controller command is considered safe merely because it appears plausible.
