# AGENTS.md

## Project

MyPlasm Inspector is a small Windows diagnostic utility for the existing Proma MyPlasm CNC controller. Keep the project focused. Do not turn it into a general CNC suite or create unnecessary governance layers.

## Source of truth

GitHub is authoritative. Before changing code:

1. Read this file.
2. Read `README.md`.
3. Read `docs/PROJECT_SCOPE.md` and `docs/SAFETY.md`.
4. Read the complete GitHub issue being implemented.
5. Inspect relevant evidence under `Old installed software/` without modifying it.

## Non-negotiable safety rule

The initial application is read-only.

Do not implement or expose:

- axis motion or homing;
- output toggling;
- plasma start;
- configuration writes;
- FTDI EEPROM writes;
- firmware upload, erase, or update;
- undocumented controller commands.

Any transmit path must pass through one centralized allowlist. Unknown or unclassified commands must fail closed before reaching `FT_Write`.

## Architecture

Keep clear boundaries between:

- Windows UI;
- FTDI transport;
- protocol framing and decoding;
- command safety policy;
- capture/evidence export;
- fake transport and tests.

The UI must not call D2XX directly. Protocol decoders must be usable without hardware. Tests must be able to run entirely offline.

## Evidence discipline

Label findings as one of:

- `confirmed` — directly supported by code, capture, runtime behavior, or vendor material;
- `hypothesis` — plausible but unproven;
- `unknown` — not yet determined.

Never promote a hypothesis to confirmed without reproducible evidence.

## Implementation quality

- Target .NET 8.
- Handle x86/x64 native-library compatibility explicitly.
- Use nullable reference types and warnings as errors where practical.
- Add tests for every safety boundary.
- Preserve raw bytes exactly in captures.
- Use UTC timestamps in exported machine-readable data.
- Do not require internet access at runtime.
- Keep dependencies minimal and documented.

## Completion

A task is complete only when:

- acceptance criteria are met;
- tests pass;
- safety tests prove blocked operations cannot reach the transport;
- documentation reflects confirmed behavior;
- no generated binaries or large evidence archives are committed accidentally.
