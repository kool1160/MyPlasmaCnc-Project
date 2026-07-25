# MyPlasm Protocol Notes

This file is the evidence ledger for controller communication.

## Confirmed

- The original Windows application uses the FTDI D2XX API.
- The installed runtime includes `ftd2xx.dll`.
- The controller is identified by the original software as `MyPlasm CNC`.
- The original logs report firmware in the form `FirmwareVer 1.2 1`.
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

- The bundled `ftd2xx.dll` referenced by project planning is not present in the current checkout, so its PE architecture has not been confirmed.
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

The structural capture facts above do not identify payload meaning, framing,
checksums, firmware fields, coordinates, status, inputs, or command safety. See
`docs/protocol-analysis.md` for the offline analyzer's deterministic evidence
rules.
