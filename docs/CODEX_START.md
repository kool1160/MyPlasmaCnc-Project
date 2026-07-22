# Codex Desktop Start

## Clone

```powershell
git clone https://github.com/kool1160/MyPlasmaCnc-Project.git
cd MyPlasmaCnc-Project
```

Open the cloned folder in Codex Desktop.

## First assignment

Implement GitHub Issue #1 in bounded steps. Begin with the application foundation and FTDI enumeration only. Do not send controller commands until the read-only safety policy and fake-transport tests exist.

## Required first actions

1. Read `AGENTS.md`.
2. Read `README.md`.
3. Read `docs/PROJECT_SCOPE.md`, `docs/SAFETY.md`, and `docs/PROTOCOL_NOTES.md`.
4. Inspect the installed-runtime evidence without modifying it.
5. Confirm the architecture of the bundled `ftd2xx.dll` before choosing x86, x64, or a dual-build strategy.
6. Create a short implementation plan tied directly to Issue #1 acceptance criteria.

## Development sequence

1. Solution and project structure.
2. Fake transport and safety policy.
3. D2XX native wrapper.
4. FTDI enumeration and identity display.
5. Safe open/close lifecycle.
6. Passive receive capture.
7. Evidence export.
8. UI polish and packaging.

## Live hardware rule

Do not perform live-controller testing until automated safety tests pass and the machine is prepared according to `docs/SAFETY.md`.
