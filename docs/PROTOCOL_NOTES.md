# MyPlasm Protocol Notes

This file is the evidence ledger for controller communication.

## Confirmed

- The original Windows application uses the FTDI D2XX API.
- The installed runtime includes `ftd2xx.dll`; vendor binaries remain outside
  Git and are supplied locally.
- The verified original D2XX DLL is PE32/x86, file version `3.01.19`, with
  SHA-256
  `381117C743766E3A696609BB29CA075772AA603CFF196E16C3854C06EE1AB254`.
- The controller is identified by the original software as `MyPlasm CNC`.
- The original logs report firmware in the form `FirmwareVer 1.2 1`.
- FTDI documents `FT_CreateDeviceInfoList` followed by `FT_GetDeviceInfoList` as the device-enumeration sequence; it does not require an open device handle.
- FTDI documents `FT_GetLibraryVersion` as a handle-free metadata operation.
- FTDI documents `FT_GetDriverVersion` as requiring an open device handle, so this task does not query it.
- A bench-isolated startup/reconnect recorder capture contains 106,759 valid
  schema-version-1 JSONL records with unique, contiguous, monotonically
  increasing sequence values 1 through 106759.
- That private capture contains 6 `FT_ListDevices`, 4 successful `FT_OpenEx`,
  4 `FT_Close`, 99,805 `FT_GetQueueStatus`, 3,472 `FT_Write`, 3,456 `FT_Read`,
  and 3 each of baud-rate, data-characteristic, flow-control, and latency-timer
  calls. It contains no `FT_SetBitMode` record.
- A deterministic same-open-handle FIFO rule pairs 3,456 successful writes and
  reads in that capture. Sixteen writes remain unmatched.
- The reconnect is structurally visible as close, enumeration, successful
  reopen, and repeated communication configuration.
- One close returned status 1 after an earlier successful close. The final
  reopened handle has no explicit close before capture end.
- The raw capture SHA-256 is
  `9BE39C4A186B92A3B7D4F8C7479205A5D19AF45FA385BE86CCA812AF29A8CE3A`
  and remains private.

## Hypotheses

- Packet framing, command identifiers, sequence handling, checksums, status masks, and coordinate encoding remain hypotheses until supported by reproducible evidence.

## Unknown

- Whether other MyPlasm installations bundle the same D2XX version and hash.
- Exact D2XX open parameters used by the known-good application.
- Exact read-only identification handshake.
- Packet boundaries and checksum behavior.
- Coordinate and input-state response formats.
- Meaning of `TXCorr:2` and `Old_TX_err`.
- Whether FIFO write/read pairs correspond to one higher-level request and
  response.
- Why the initial 16 writes have no paired reads.
- Why the final reopened handle lacks an explicit close.

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

- A tracked-tree search found no `ftd2xx.dll` under
  `Old installed software/` or elsewhere in tracked source.
- `git rev-list --objects --all`, full-history path searches, and the original evidence commit `164e638` contain no `ftd2xx.dll`, `.dll`, or `.exe` runtime file.
- The evidence commit predates `.gitignore`; it added logs, firmware/configuration/job data, and screenshots, but not the installed executable runtime.
- Commit `4d73c5c` later added ignore rules for `native/local/` and `ftd2xx.dll`, ensuring locally supplied vendor libraries are not committed.
- The repository has no Git LFS entry or sparse-checkout rule hiding the DLL.
- Git history cannot prove why the original evidence upload omitted the runtime binary. It proves only that the DLL was never committed and that later policy intentionally keeps local copies out of Git.

Safety impact: the loader does not trust the documented architecture alone. A
local DLL is inspected for PE architecture, DLL characteristics, file version,
SHA-256, and current/selected process compatibility before native loading.

The later locally obtained original DLL matched the recorder evidence and the
confirmed x86/version/hash values above. The DLL remains ignored and was not
added to Git.

## Portable startup diagnostic — 2026-07-23

Classification: `confirmed` for the previous application's eager startup
design; `unknown` for the exact target-PC crash mechanism until its new startup
log is collected.

- The prior WPF window automatically ran fake transport enumeration in
  `Window_Loaded`, before the operator could interact with the window.
- The prior package did not force WPF software rendering before window creation
  and did not write an application startup log before `MainWindow`
  construction.
- The diagnostic package forces software rendering by default, logs environment
  and startup stages before `MainWindow` exists, and defers every transport
  action until an explicit button click.
- Startup logging failures are isolated from application startup. Persistent
  file logging falls back once to a bounded in-memory/Trace diagnostic path and
  does not retry a failed file sink.
- Portable packaging accepts only the confirmed D2XX DLL architecture,
  version, size, and SHA-256 above. A package manifest binds generated
  application and DLL evidence to an exact clean source commit.

Safety impact: no controller operation is attempted during startup. The exact
root cause of the target PC's renderer/startup crash remains unknown pending
its diagnostic evidence; this change removes automatic transport work,
prevents logging failures from becoming startup failures, and provides
reproducible package evidence.

## Passive receive hardening — 2026-07-26

Classification: `confirmed` for code structure and deterministic offline tests;
`unknown` for live Inspector receive behavior because this reconciliation did
not access a controller.

- The passive production surface contains enumeration, exact-serial open,
  driver metadata, queue status, receive read, and close only.
- No `FT_Write`, communication configuration, reset, purge, EEPROM, firmware,
  controller request, protocol decoder, or replay function is present.
- Native injection and the process-detector bypass are internal test seams, not
  public application APIs.
- Capture uses monotonic elapsed time and stops at five minutes, 64 MiB, or
  100,000 events. Returned counts are checked before bytes are copied.
- A close failure is terminal for that process and blocks another enumeration
  or open.
- Export streams raw bytes and compact JSONL, omits local D2XX source paths
  from structured metadata, hashes every evidence file, validates a staged ZIP
  by reopening it, and publishes the final ZIP only after validation.
- Automated tests use synthetic bytes and injected native behavior only.

Safety impact: this slice can preserve bytes already waiting in the FTDI
receive queue without originating traffic. A zero-byte result remains valid
evidence and does not justify adding a request to the empty command allowlist.

## Vendor references

- FTDI D2XX Programmer's Guide: <https://ftdichip.com/wp-content/uploads/2025/06/D2XX_Programmers_Guide.pdf>
- FTDI `FT_GetLibraryVersion`: <https://www.ftdichip.com/Support/Knowledgebase/ft_getlibraryversion.htm>
- FTDI `FT_GetDriverVersion`: <https://www.ftdichip.com/Support/Knowledgebase/ft_getdriverversion.htm>

The structural capture facts above do not identify payload meaning, framing,
checksums, firmware fields, coordinates, status, inputs, or command safety. See
`docs/protocol-analysis.md` for the offline analyzer's deterministic evidence
rules.
