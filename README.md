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

The first bounded Issue #1 slice contains:

- `MyPlasm.Inspector.Core` — transport contracts and the centralized command safety boundary;
- `MyPlasm.Inspector.Transport.Fake` — deterministic, hardware-free FTDI behavior;
- `MyPlasm.Inspector.App` — an initial WPF shell that uses only the fake transport;
- `MyPlasm.Inspector.Tests` — offline fake-transport and safety-policy tests.

The production allowlist is intentionally empty because no controller request bytes are confirmed yet. The app does not load D2XX and cannot access controller hardware in this slice.

### Build and test

Install a .NET 8 SDK, then run from the repository root:

```powershell
dotnet restore MyPlasm.Inspector.sln
dotnet build MyPlasm.Inspector.sln --configuration Release --no-restore
dotnet test MyPlasm.Inspector.sln --configuration Release --no-build
```

Launch the fake-only Windows shell with:

```powershell
dotnet run --project src/MyPlasm.Inspector.App/MyPlasm.Inspector.App.csproj
```

## Ground rule

Confirmed facts, hypotheses, and unknowns must be labeled separately. No controller command is considered safe merely because it appears plausible.

## FTD2XX protocol recorder

The repository also contains a bounded native reverse-engineering tool: a
32-bit forwarding `ftd2xx.dll` that records calls made by the original
32-bit MyPlasm application while forwarding them to a same-directory
`ftd2xx_real.dll`.

This recorder is not part of the replacement Inspector transport and does not
generate, decode, filter, approve, or rewrite controller commands. It forwards
only calls initiated by the original application. Because those vendor-originated
calls may include motion or output commands, the first live startup-only capture
requires the motor supply, plasma source, and torch-start connection to be
disabled as documented.

Supported build environment:

- Windows with Visual Studio 2019 or 2022 C++ Build Tools;
- CMake 3.21 or newer;
- the Win32/x86 MSVC toolchain.

Configure, build, and test:

```powershell
cmake -S tools/ftd2xx-protocol-recorder -B build/protocol-recorder -G "Visual Studio 17 2022" -A Win32
cmake --build build/protocol-recorder --config Release
ctest --test-dir build/protocol-recorder -C Release --output-on-failure
```

No vendor executable, DLL, driver, firmware, controller, or plasma table is
needed for these tests. Capture writes are serialized and physically flushed
only at a 64 KiB threshold, a one-second threshold, or after `FT_Close`; an
abnormal termination can therefore lose the final buffered records. Installer
and restoration rollback use verified transaction copies and preserve
unexpected files under unique quarantine names. See
[docs/protocol-recorder.md](docs/protocol-recorder.md) for the architecture,
log schema, transactional installation and restoration, and the required first
live capture procedure.
