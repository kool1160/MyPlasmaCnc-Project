# Three-Run Differential Capture Campaign

## Purpose and boundary

This is the operator procedure for collecting three independent, structurally
comparable MyPlasm startup/reconnect captures and comparing their sanitized
offline analyses. It is not authorization to decode, replay, generate, or
classify controller commands.

The campaign has not been performed by this implementation. An operator must
complete every physical gate and evidence step on the isolated controller.
Raw captures, analyzer outputs, installation records, and local paths remain
private and outside Git.

## Required physical isolation gate

Confirm every item immediately before every run:

1. Everlast/plasma source is OFF.
2. Torch-start connection is physically disabled or unplugged.
3. 48 V motor supply is OFF.
4. Motor drives, machine outputs, and plasma interface are disconnected where
   practical.
5. MyPlasm controller 24 V supply is ON.
6. USB is connected.
7. Original MyPlasm application is closed before recorder installation or
   restoration work.

Do not proceed from a previous confirmation, software state, or screenshot.
The operator must physically confirm the current state.

No jog, home, torch, output, configuration, firmware, EEPROM, or
machine-control action is permitted during the campaign. Do not use MyPlasm
Inspector for this campaign and do not originate independent FTDI traffic.
Only the original application may make calls through the observational
recorder.

## Prerequisites

Before run 1:

- use the reviewed x86 recorder artifact and its matching
  `build-manifest.json`, `SHA256SUMS.txt`, and source commit;
- read [protocol-recorder.md](protocol-recorder.md), including the
  transactional install and restoration procedures;
- verify the original application, original D2XX DLL, proxy, artifact, and
  scripts against the reviewed hashes;
- prepare three new private capture locations and three new, separate analysis
  output directories;
- prepare the metadata worksheet below; and
- choose one normal reconnect UI action and use only that same action in every
  run.

Never copy a raw capture, vendor binary, private analysis, local installation
manifest, or machine-local path into this repository.

## Identical sequence for each run

Perform these steps exactly once for run 1, run 2, and run 3. Keep the physical
isolation gate in force throughout. Record any timing or action deviation in
the worksheet; never hide, correct, or silently discard a deviation.

1. Confirm the seven physical isolation items again. Verify the original DLL
   is active, then use only the existing hash-aware `install-proxy.ps1` with
   the explicit application directory and reviewed artifact proxy. Preserve
   its output and verify the proxy, preserved original, installation manifest,
   and hashes using the existing scripts and recorder documentation. Stop on
   any ambiguous, quarantine, or partial state.
2. Start a fresh capture directory and recorder session. Do not reuse or
   append to an earlier capture.
3. Launch the original MyPlasm application.
4. Wait for its normal communication to establish. Record the displayed
   communication result without using a machine-control action.
5. Leave the application idle for 60 seconds.
6. Trigger exactly one normal reconnect using the same documented UI action
   in every run. Do not interact with any other control.
7. Leave the application idle for another 60 seconds.
8. Close the original application normally and confirm its process has exited.
9. Restore the original D2XX DLL with the existing hash-aware
   `restore-original.ps1` procedure. Verify its hash and restoration
   idempotence. Treat each run as a complete campaign session and restore
   before preparing the next run.
10. Preserve the private raw capture unchanged. Record its SHA-256, byte size,
    nonempty JSONL record count, recorder source commit, proxy hash, UTC start
    and end timestamps, operator notes, and every deviation. Validate every
    nonempty line as JSON and confirm sequence values are unique, increasing,
    and contiguous; stop if they are not.
11. Analyze the preserved capture into its own empty analysis directory using
    the explicit expected hash:

    ```powershell
    dotnet run --project tools/MyPlasm.ProtocolAnalyzer -- analyze `
      --input "C:\PrivateEvidence\run-1\traffic.jsonl" `
      --output "C:\PrivateEvidence\run-1\analysis" `
      --expected-sha256 "<RUN-1-TRAFFIC-SHA256>"
    ```

    Substitute only the current run number, private path, and verified capture
    SHA-256. Preserve all six sanitized analyzer outputs unchanged.
12. After all three separate analyses pass, run `compare` only against the
    three sanitized analysis directories:

    ```powershell
    dotnet run --project tools/MyPlasm.ProtocolAnalyzer -- compare `
      --analysis "C:\PrivateEvidence\run-1\analysis" `
      --analysis "C:\PrivateEvidence\run-2\analysis" `
      --analysis "C:\PrivateEvidence\run-3\analysis" `
      --output "C:\PrivateEvidence\campaign-comparison"
    ```

The comparison validates each sanitized report set before using it. It does
not accept `traffic.jsonl`, a ZIP, payload file, vendor DLL, firmware, or other
capture artifact.

## Private campaign worksheet

Complete one row per independent run and keep it with the private evidence:

| Field | Run 1 | Run 2 | Run 3 |
| --- | --- | --- | --- |
| Physical gate confirmed UTC | | | |
| Artifact/workflow identity | | | |
| Recorder source commit | | | |
| Proxy SHA-256 | | | |
| Original D2XX SHA-256 | | | |
| Capture start UTC | | | |
| Capture end UTC | | | |
| `traffic.jsonl` SHA-256 | | | |
| `traffic.jsonl` byte size | | | |
| Nonempty JSONL record count | | | |
| Communication result | | | |
| 60-second pre-reconnect idle actual | | | |
| Reconnect UI action | | | |
| 60-second post-reconnect idle actual | | | |
| Normal close confirmed | | | |
| Restoration hash/idempotence verified | | | |
| Analyzer result and six outputs verified | | | |
| Operator notes and deviations | | | |

## Immediate stop conditions

Stop the current run and do not continue or improvise if any of these occurs:

- unexpected motion, homing, output activation, torch behavior, or plasma
  behavior;
- original software instability or controller communication behavior
  materially different from the known baseline;
- recorder installation/restoration hash mismatch or ambiguous state;
- malformed or noncontiguous recorder evidence;
- failed analysis hash verification;
- a missing expected output;
- unexpected transmit generated by MyPlasm Inspector rather than the original
  vendor application; or
- any uncertainty about physical isolation.

Preserve the exact state, console output, and private evidence for review. Do
not repair DLL state manually, repeat a failed capture automatically, or
substitute locally rebuilt recorder files.

## Evidence handling and interpretation

Keep raw captures and private analyzer outputs outside Git and do not attach
them to a public issue or pull request. They can contain controller
identifiers, machine state, protocol payloads, timestamps, and structural
fingerprints.

The comparison reports only deterministic structural evidence:

- `confirmed` means the stated count, fingerprint, hash, timing statistic, or
  cross-run presence follows directly from the three verified report sets;
- `hypothesis` is not generated by this command and requires a separate,
  explicit evidence rule and review; and
- `unknown` includes packet framing, fields, counters, checksums, semantics,
  command safety, and replay suitability.

The phrase `stable across all three captures` means only that the exact
sanitized transaction-class fingerprint occurs in every verified input. It
does not establish meaning, safety, causation, or permission to replay bytes.
