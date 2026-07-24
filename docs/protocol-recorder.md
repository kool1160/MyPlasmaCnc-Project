# MyPlasm FTD2XX Protocol Recorder

## Purpose and scope

The protocol recorder is a native 32-bit Windows forwarding DLL used to observe
the original MyPlasm application's established D2XX behavior. It is a bounded
reverse-engineering aid, not a controller implementation.

It does not:

- generate independent FTDI or controller calls;
- decode, classify, filter, approve, or rewrite protocol packets;
- implement motion, homing, probing, torch, THC, output, or firmware behavior;
- open a controller unless the original application calls the forwarded open
  function;
- modify the Inspector's empty production command allowlist.

The recorder does forward calls made by the original vendor application,
including writes and FTDI configuration calls. Its safety depends on using the
startup-only procedure below with all motion and plasma energy isolated.

## Why a forwarding DLL

Static evidence shows that the PE32 `MyPlasmCNC.exe` imports 11 functions from a
PE32 `FTD2XX.DLL`. Windows resolves a DLL beside the application before the
system driver location. The installation script therefore:

1. renames the verified original `ftd2xx.dll` to `ftd2xx_real.dll`;
2. places the recorder beside the application as `ftd2xx.dll`;
3. records hashes in `myplasm-proxy-install.json`.

The recorder derives its own module directory, constructs an absolute path to
`ftd2xx_real.dll`, loads it lazily on the first exported call, resolves all
required exports once, and forwards the original arguments and return values.
There is no custom `DllMain`; filesystem, loading, synchronization, and logging
work occur only after an exported function is called.

## Exact forwarded surface

The x86 `.def` file exports exactly these undecorated names:

- `FT_ListDevices`
- `FT_OpenEx`
- `FT_Close`
- `FT_Read`
- `FT_Write`
- `FT_SetBaudRate`
- `FT_SetDataCharacteristics`
- `FT_SetFlowControl`
- `FT_GetQueueStatus`
- `FT_SetLatencyTimer`
- `FT_SetBitMode`

No EEPROM, purge, reset, firmware, or other D2XX API is exported or resolved.

## Forwarding and failure model

- Initialization uses Windows one-time initialization and is thread-safe.
- The real DLL is loaded only from the proxy's directory by absolute path.
- All 11 exports must resolve before forwarding is enabled.
- A missing/corrupt real DLL or missing export produces `FT_OTHER_ERROR`.
  Output handles and byte/count outputs are set to safe empty values only when
  no real call can occur.
- Successful real calls receive the vendor application's original pointers,
  values, handles, buffers, and counts.
- The logger is synchronized independently and cannot change the status returned
  by the real DLL.
- Log formatting, directory creation, file opening, allocation, or write failure
  is caught and ignored by the forwarding path.
- A thread-local logger guard prevents recursive logging.
- Stable numeric handle IDs avoid treating raw process handles as cross-session
  identifiers.

The recorder does not claim live compatibility until a startup-only capture is
completed and reviewed.

## JSON Lines capture

Default location:

```text
%LOCALAPPDATA%\MyPlasmProtocolRecorder\captures\<UTC timestamp>\traffic.jsonl
```

To choose a different capture directory, set `MYPLASM_PROXY_LOG_DIR` before
starting the original application. The value is treated as the directory that
will contain `traffic.jsonl`:

```powershell
$env:MYPLASM_PROXY_LOG_DIR = 'D:\MyPlasmEvidence\capture-001'
```

Each line is one complete JSON object. Common fields are:

| Field | Meaning |
| --- | --- |
| `schema_version` | Integer schema version, currently `1` |
| `session_id` | Stable GUID for the loaded proxy session |
| `utc_timestamp` | UTC ISO-8601 wall-clock timestamp |
| `elapsed_us` | Microseconds from monotonic process-local capture start |
| `process_id` / `thread_id` | Calling process and thread |
| `function` | Exact D2XX export name |
| `sequence` | Unique monotonically increasing JSONL record order |
| `handle_id` | Stable recorder-assigned handle identifier; zero when absent |
| `status` | Unmodified FT status returned by the real DLL |
| `arguments` | Applicable input arguments |
| `flush_trigger` | `none`, `byte_threshold`, `time_threshold`, or `close` |

Function-specific records include:

- requested and actual byte counts;
- complete uppercase hexadecimal `write_hex` and `read_hex` payloads;
- queue count;
- baud rate;
- latency timer;
- bit-mode mask and mode;
- flow-control mode, XON, and XOFF;
- word length, stop bits, and parity;
- device-list count and open selector metadata.

Payloads are not truncated. Concurrent writes are serialized under one lock so
JSON objects cannot interleave. The recorder uses cached file writes and calls
`FlushFileBuffers` only after at least 64 KiB has accumulated, after at least
one second has elapsed since the previous successful flush, or immediately
after writing an `FT_Close` record. It does not physically flush after every
FTDI API call. The threshold check and physical flush remain inside the same
log lock as the associated record.

Logging and flushing are best effort. A disk or logging failure cannot alter
the real D2XX status, output buffer, byte count, or successful forwarding
behavior. Abnormal process or operating-system termination can lose the final
records that were written to the operating-system cache but had not reached a
threshold. A normal `FT_Close` minimizes this exposure by forcing all records
through the close record to stable storage.

## Build and test

Prerequisites:

- Windows;
- Visual Studio 2019 or 2022 C++ Build Tools with x86 support;
- CMake 3.21 or newer.

Visual Studio 2022 commands:

```powershell
cmake -S tools/ftd2xx-protocol-recorder -B build/protocol-recorder -G "Visual Studio 17 2022" -A Win32
cmake --build build/protocol-recorder --config Release
ctest --test-dir build/protocol-recorder -C Release --output-on-failure
```

The proxy is produced at:

```text
build\protocol-recorder\Release\ftd2xx.dll
```

The tests use only the repository's synthetic mock:

- normal forwarding through all 11 functions;
- exact output/export and x86 PE verification;
- known read and write payload fidelity;
- configuration and queue logging;
- valid JSONL and stable handle IDs;
- missing real DLL;
- missing required export;
- corrupt real DLL/failed initialization;
- concurrent calls and sequence uniqueness;
- high-volume mixed queue/read/write stress without deadlock or malformed JSON;
- 64 KiB, one-second, and `FT_Close` flush triggers;
- re-entrant calls without recursion or deadlock;
- unavailable logging directory;
- mock FTDI error status;
- empty read;
- zero-byte write;
- untruncated 1 MiB payload;
- hash/state-aware installation and restoration;
- injected post-move, post-copy, post-verification, and manifest-write
  transaction failures with exact file/hash/state assertions.

CI performs the same Win32 build and tests on `windows-2022`. It does not
download, execute, or upload vendor software and requires no hardware.

## Hash-aware installation

Close the original MyPlasm application first. Supply one explicit installation
directory and the built proxy:

```powershell
.\tools\install-proxy.ps1 `
  -InstallationDirectory 'C:\Path\To\MyPlasmCNC' `
  -ProxyDllPath 'C:\Path\To\build\protocol-recorder\Release\ftd2xx.dll'
```

The script defaults to the verified hashes:

- `MyPlasmCNC.exe`:
  `0ec9f20ca46fb882257c610b25790e79474fa8f882a97d6b524e1b7b7b1447a9`
- original `ftd2xx.dll`:
  `381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254`

It prints all relevant hashes, checks directory writability, rejects ambiguous
or partial states, and refuses to overwrite `ftd2xx_real.dll`. Before moving
the original it creates and verifies a uniquely named transaction backup. It
then verifies every moved/copied DLL before publishing
`myplasm-proxy-install.json`.

If any post-copy or post-move step fails, the script restores the verified
original as `ftd2xx.dll` whenever possible. Copied, unexpected, temporary, and
failed-verification files are moved to uniquely named sibling paths containing
`.quarantine-`; they are never overwritten or silently deleted during failed
rollback. The script prints every resulting standard and quarantine path,
SHA-256, and whether the final DLL arrangement is a verified runnable state.
Running it again after a completed install or a successfully rolled-back
failure is idempotent. Administrator rights are unnecessary when the
application directory is user-writable.

Do not use the test-only expected-hash override parameters for a live
installation unless separately reviewed evidence establishes different trusted
binaries.

`MyPlasmCNC.pfw` SHA-256
`be2fa39a2d97cc1259f2509800828b0fe224bfa158d271cefb346489061e9569`
is recorded evidence only. Neither installation script nor proxy opens, parses,
copies, rewrites, or otherwise handles that firmware file.

## Restoration

Close the original application, then run:

```powershell
.\tools\restore-original.ps1 `
  -InstallationDirectory 'C:\Path\To\MyPlasmCNC'
```

The restoration script:

1. verifies the active DLL hash against the proxy hash recorded at installation;
2. verifies `ftd2xx_real.dll` against both the recorded and expected original
   hashes;
3. verifies the application hash has not changed;
4. creates verified transaction backups of both DLLs before either move;
5. moves the proven proxy aside and restores the proven original DLL;
6. verifies both post-move hashes;
7. atomically changes the manifest to state `restored`;
8. removes only verified redundant transaction copies;
9. leaves all capture directories untouched.

If a move, hash verification, or manifest update fails, the script reconstructs
the prior verified proxy-plus-original arrangement from the verified files or
transaction backups. Unknown or failed-verification files are preserved under
unique `.quarantine-` names. It never makes an unknown DLL active or reports a
safe restoration unless the active arrangement's hashes prove it is runnable.
The complete resulting paths and hashes are printed even after rollback.

Running restoration again reports that the original DLL is already active.
Unknown, missing-manifest, hash-mismatched, or partial states are refused rather
than guessed.

### Verify restoration

After restoration:

- `ftd2xx.dll` exists and its SHA-256 is
  `381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254`;
- `ftd2xx_real.dll` no longer exists;
- `myplasm-proxy-install.json` has state `restored`;
- capture directories still exist;
- a second restoration reports that no change is needed.

## First live startup-only capture procedure

1. Turn the Everlast plasma source OFF.
2. Disable or physically unplug the torch-start connection.
3. Turn the 48 V motor supply OFF.
4. Leave the MyPlasm 24 V controller supply ON.
5. Connect USB.
6. Install the proxy using the provided script.
7. Start the original MyPlasm application.
8. Wait for the communication status.
9. Leave the application idle for approximately 30 seconds.
10. Trigger one communication reconnect using the original application.
11. Wait approximately 10 seconds.
12. Close the application normally.
13. Restore the original FTDI DLL.
14. Preserve the generated capture directory.
15. Do not attempt jogging, homing, probing, plasma activation, or toolpath execution during the first capture.

Do not deviate from this sequence for the initial capture.

## Successful-capture indicators

A successful capture has:

- a new timestamped capture directory;
- a nonempty `traffic.jsonl`;
- every nonempty line parses as one JSON object;
- `FT_ListDevices` and `FT_OpenEx` records from application startup;
- a stable `session_id`;
- increasing unique `sequence` values;
- consistent nonzero `handle_id` values after a successful open;
- exact read/write payload hex and byte counts where communication occurred;
- an `FT_Close` record after normal application shutdown.

Preserve the complete original capture directory outside Git. Do not commit USB
or protocol captures that may contain private machine data.

## Troubleshooting

- **`FT_OTHER_ERROR` immediately:** confirm `ftd2xx_real.dll` is present beside
  the proxy and that it is the original x86 DLL.
- **Application will not start:** restore immediately; confirm the proxy is x86
  and exports exactly the 11 required names.
- **No log appears:** check `MYPLASM_PROXY_LOG_DIR`, directory permissions, free
  disk space, and `%LOCALAPPDATA%`.
- **Partial installation warning:** do not rename files manually. Preserve the
  directory, hashes, and error text for review.
- **Restoration refuses:** do not delete either DLL. The refusal means the
  recorded hashes or state do not prove a safe destructive action.
- **Log ends unexpectedly:** preserve it unchanged. A crash or storage failure
  can leave the final record incomplete.

## Known limitations

- Live-controller compatibility is unconfirmed until a capture is completed and
  reviewed.
- The recorder observes only these 11 imported D2XX functions.
- It cannot see USB traffic below D2XX or calls made through other libraries.
- Synchronous durable logging adds latency and consumes disk space, especially
  for large payloads.
- Logging is best effort; forwarding continues if evidence storage fails.
- Protocol meaning, framing, checksums, and command safety are intentionally not
  decoded in this task.
