# MyPlasm Protocol Notes

This file is the evidence ledger for controller communication.

## Confirmed

- The original Windows application uses the FTDI D2XX API.
- The controller is identified by the original software as `MyPlasm CNC`.
- The original logs report firmware in the form `FirmwareVer 1.2 1`.
- FTDI documents `FT_CreateDeviceInfoList` followed by `FT_GetDeviceInfoList` as the device-enumeration sequence; it does not require an open device handle.
- FTDI documents `FT_GetLibraryVersion` as a handle-free metadata operation.
- FTDI documents `FT_GetDriverVersion` as requiring an open device handle, so enumeration does not query it.
- Live target validation confirmed one FTDI device, one exact `MyPlasm CNC` candidate, and enumeration with no open or transmit.
- The passive-receive implementation can open only the exact unique serial from a
  completed enumeration, query driver metadata, poll receive queue depth, and read
  already-queued bytes. It has no controller transmit or FTDI configuration API.
- This implementation has been validated only with injectable offline native
  doubles during hardening; no real controller was opened for this correction.

## Hypotheses

- Packet framing, command identifiers, sequence handling, checksums, status masks, and coordinate encoding remain hypotheses until supported by reproducible evidence.

## Unknown

- Project planning states that the installed runtime included `ftd2xx.dll`, but no copy is available in the working tree or reachable Git history; its architecture, file version, and SHA-256 therefore remain unknown.
- Exact D2XX open parameters used by the known-good application.
- Exact read-only identification handshake.
- Packet boundaries and checksum behavior.
- Coordinate and input-state response formats.
- Meaning of `TXCorr:2` and `Old_TX_err`.

## Evidence entry format

For each finding, record:

- date and investigator;
- source file, capture, or test;
- exact bytes or code location;
- reproduction steps;
- classification: confirmed, hypothesis, or unknown;
- safety impact;
- related tests.

Do not place undocumented command bytes into application code before they are classified and reviewed.

The application allowlist is currently empty. Byte values used by automated tests are explicitly synthetic sentinels and are not protocol evidence.

## Repository evidence audit — 2026-07-22

Classification: `confirmed` for repository state; `unknown` for the reason the original upload omitted the DLL.

- A recursive working-tree search found no `ftd2xx.dll` under `Old installed software/` or elsewhere outside generated build output.
- `git rev-list --objects --all`, full-history path searches, and the original evidence commit `164e638` contain no `ftd2xx.dll`, `.dll`, or `.exe` runtime file.
- The evidence commit predates `.gitignore`; it added logs, firmware/configuration/job data, and screenshots, but not the installed executable runtime.
- Commit `4d73c5c` later added ignore rules for `native/local/` and `ftd2xx.dll`, ensuring locally supplied vendor libraries are not committed.
- The repository has no Git LFS entry or sparse-checkout rule hiding the DLL.
- Git history cannot prove why the original evidence upload omitted the runtime binary. It proves only that the DLL was never committed and that later policy intentionally keeps local copies out of Git.

Safety impact: no architecture assumption is made. A local DLL is inspected for PE architecture, file version, SHA-256, and current/selected process compatibility before native loading.

## Local runtime evidence audit - 2026-07-23

Classification: `confirmed` for the inspected local runtime file; this does not
change any controller-protocol classification.

- A legally obtained `ftd2xx.dll` was located in the locally installed MyPlasmCNC
  runtime, outside the Git working tree.
- PE inspection reported `x86`, file version `3.01.19`, and SHA-256
  `381117C743766E3A696609BB29CA075772AA603CFF196E16C3854C06EE1AB254`.
- The PE inspection utility confirmed compatibility with the selected `win-x86`
  application architecture.
- The DLL is copied only to ignored local staging and generated portable-package
  output; it is not committed to Git.

Safety impact: the vendor DLL makes D2XX metadata enumeration available in the
portable package. It does not authorize device opening or any controller protocol
operation. The production command allowlist remains empty.

## Portable startup diagnostic - 2026-07-23

Classification: `confirmed` for the previous application's eager startup design;
`unknown` for the exact target-PC crash mechanism until its new startup log is
collected.

- The prior WPF window automatically ran fake transport enumeration in
  `Window_Loaded`, before the operator could interact with the window.
- The prior package did not force WPF software rendering before window creation and
  did not write an application startup log before `MainWindow` construction.
- The diagnostic package forces software rendering by default, logs environment and
  startup stages before `MainWindow` exists, and defers every transport action until
  an explicit button click.

Safety impact: no controller operation is attempted during startup. The exact root
cause of the target PC's renderer/startup crash remains unknown pending its startup
log; this change removes automatic transport work and provides reproducible evidence.

## Passive receive hardening — 2026-07-23

Classification: `confirmed` for code structure and offline automated behavior;
`unknown` for live controller receive behavior because no hardware test was run.

- Enumeration, passive handle ownership, capture lifecycle, and evidence export
  are separate components.
- Automated tests cover exact-device rejection, exact-serial open, process
  exclusion, handle state, close failures, queue polling, zero-depth evidence,
  byte ordering, malformed native counts, cancellation, concurrent-capture
  rejection, close-during-capture ordering, zero-byte export, ZIP hashes, and
  the empty transmit surface.
- Each capture event records UTC time, operation, queue depth, requested and
  returned counts, D2XX status, elapsed session time, and an error message when
  applicable.
- No protocol decoder, request packet, controller command, configuration change,
  or transmit allowlist entry was added.

Safety impact: passive receive can be exercised without inventing or sending a
controller request. The production command allowlist remains empty.

## Vendor references

- FTDI D2XX Programmer's Guide: <https://ftdichip.com/wp-content/uploads/2025/06/D2XX_Programmers_Guide.pdf>
- FTDI `FT_GetLibraryVersion`: <https://www.ftdichip.com/Support/Knowledgebase/ft_getlibraryversion.htm>
- FTDI `FT_GetDriverVersion`: <https://www.ftdichip.com/Support/Knowledgebase/ft_getdriverversion.htm>
