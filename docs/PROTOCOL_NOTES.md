# MyPlasm Protocol Notes

This file is the evidence ledger for controller communication.

## Confirmed

- The original Windows application uses the FTDI D2XX API.
- The installed runtime includes `ftd2xx.dll`.
- The controller is identified by the original software as `MyPlasm CNC`.
- The original logs report firmware in the form `FirmwareVer 1.2 1`.

## Hypotheses

- Packet framing, command identifiers, sequence handling, checksums, status masks, and coordinate encoding remain hypotheses until supported by reproducible evidence.

## Unknown

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
