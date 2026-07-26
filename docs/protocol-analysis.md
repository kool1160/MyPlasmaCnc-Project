# Offline MyPlasm Protocol Capture Analysis

## Purpose and boundary

`MyPlasm.ProtocolAnalyzer` validates and structurally analyzes JSON Lines
captures produced by the repository's FTD2XX recorder. It is an offline evidence
tool. It does not identify packet meaning or approve any packet for replay.

The analyzer:

- opens only the explicitly supplied capture file and report directory;
- never enumerates or opens an FTDI device;
- never loads `ftd2xx.dll`;
- has no reference to D2XX interop, the native recorder, the WPF application,
  controller transports, or the production command gateway;
- never sends, replays, rewrites, or generates controller bytes;
- leaves the production command allowlist empty.

The analysis library is
`src/MyPlasm.Inspector.ProtocolAnalysis`. The command-line entry point is
`tools/MyPlasm.ProtocolAnalyzer`. Hardware-free tests are in
`tests/MyPlasm.Inspector.ProtocolAnalysis.Tests`.

## Run the analyzer

Use an explicit input file and output directory:

```powershell
dotnet run --project tools/MyPlasm.ProtocolAnalyzer -- analyze `
  --input "C:\PrivateEvidence\traffic.jsonl" `
  --output "C:\PrivateEvidence\analysis" `
  --expected-sha256 "9BE39C4A186B92A3B7D4F8C7479205A5D19AF45FA385BE86CCA812AF29A8CE3A"
```

The analyzer accepts plain `traffic.jsonl`. ZIP extraction is deliberately out
of scope. `--expected-sha256` is optional, but recommended for evidence work.
When supplied, the hash is checked before schema analysis or output creation.

A nonempty output directory is refused by default. `--overwrite` explicitly
allows replacement of the six known analyzer report files. Unrelated files are
not deleted. It never permits one of those report paths to be the input path.

### Input-evidence collision protection

Before hashing or parsing the capture, the analyzer compares the input against
all six report destinations. The comparison uses absolute normalized paths, is
conservatively case-insensitive, and resolves existing symbolic-link or
junction components. A collision fails with exit code `5` before the output
directory or any report is created. This protection applies with and without
`--overwrite`; the option cannot authorize replacing input evidence.

Report staging also uses a new, uniquely named file opened with exclusive
creation for each write. Existing predictable `.tmp` files are never opened,
truncated, or reused. The collision check is repeated immediately before each
staged report is committed to its final path. Staging files created by the
analyzer are removed after success or a handled write failure.

The tool prints a progress line every 25,000 validated records and a final
summary. It never prints payload bytes.

Exit codes:

| Code | Meaning |
| ---: | --- |
| `0` | Analysis and report generation succeeded |
| `1` | Unexpected failure or cancellation |
| `2` | Usage, path, or argument error |
| `3` | Expected input SHA-256 did not match |
| `4` | JSONL or recorder-schema validation failed |
| `5` | Output directory or report write failed |

Administrator rights, hardware, a native library, and internet access are not
required.

## Input and streaming validation

The input is hashed with a sequential file stream and then processed with an
asynchronous line reader. The analyzer does not call `File.ReadAllText`,
`File.ReadAllLines`, or an equivalent whole-capture API. Raw input is never
modified or normalized.

Every nonempty line must be one UTF-8 JSON object using recorder schema version
`1`. Diagnostics include the physical line number. Validation covers:

- canonical recorder session GUID and one consistent session and process;
- parseable UTC timestamp and nonnegative elapsed time;
- positive process, thread, and sequence identifiers;
- nonnegative status and recorder handle identifiers;
- unique, strictly increasing sequence values;
- one of the recorder's 11 documented function names;
- required `arguments`, `flush_trigger`, and function-specific fields;
- integer ranges for D2XX values and byte counts;
- hexadecimal text containing only ASCII hexadecimal digits and an even length;
- write payload length equal to `requested_count`;
- read payload length equal to `actual_count`, with `actual_count` no greater
  than `requested_count`;
- agreement between duplicated configuration values in `arguments` and their
  documented top-level objects.

Sequence gaps are preserved and reported; they are not silently filled. Unknown
additional JSON fields are ignored so later schema extensions do not expose
private values or disturb confirmed fields. Unsupported schema versions,
missing fields, wrong types, malformed JSON, inconsistent counts, and invalid
UTF-8 fail closed.

## Deterministic phase rules

`phase-timeline.csv` uses call evidence only:

- `process_start_pre_open` spans capture start through the record before the
  first successful `FT_OpenEx`;
- every `FT_ListDevices` is an `enumeration_attempt`;
- each `FT_OpenEx` is `open_success` or `open_failed` from its returned status;
- each successful configuration call is a `configuration_call` on its current
  sanitized handle session; row order is the configuration sequence;
- a `sustained_exchange_interval` spans the first paired write through the last
  paired read in one open-handle session;
- a close is `close_success`, `close_failed`, or `redundant_close` according to
  returned status and whether the recorder handle is currently open;
- a successful open after a successful close creates a
  `reconnect_transition`;
- successful opens without a later successful close are
  `unclosed_handle_at_capture_end`;
- records after the final successful close, while no handle is open, form
  `process_end_tail`.

Every phase is labeled `confirmed` and includes its evidence rule. These labels
describe structure, not controller behavior.

## Deterministic transaction rule

For each currently open recorder handle:

1. A successful `FT_Write` is appended to a first-in, first-out queue.
2. Queue-status polls may occur without changing the queue.
3. A successful `FT_Read` consumes the oldest queued write on the same handle.
4. A successful close ends the pairing boundary and leaves queued writes
   unmatched.
5. A later open creates a new sanitized handle session even if the recorder
   reuses the same numeric handle.
6. A successful read with no pending write is an unexpected read.
7. Failed reads and writes are counted but never paired.

Matched pairs are `confirmed` only as results of this mechanical rule.
Unmatched writes, unexpected reads, negative elapsed-time differences, failed
calls, redundant closes, and unclosed handles remain explicit observations.
The analyzer makes no higher-level protocol claim from a pair.

## Structural statistics

Reports include:

- function and return-status counts;
- sanitized open-handle session timelines;
- D2XX configuration values and recorded sequence/timestamp;
- queue-poll counts and observed durations;
- write/read requested and actual byte-count distributions;
- transaction latency, per-transaction queue polls, per-function cadence, and
  transaction cadence;
- exact payload classes by direction, length, and SHA-256;
- exact transaction classes by their write/read class fingerprints;
- class count and first/last sequence;
- class frequencies for each sanitized open-handle session;
- same-length per-byte unique-value counts and Shannon entropy;
- fixed prefix and suffix lengths observed within each same-length family.

Median is the middle value, or the mean of the two middle values. P95 and P99
use the nearest-rank rule. Means are rounded to three decimal places; entropy is
rounded to six.

No checksum search, framing suggestion, semantic field suggestion, command
classification, or replay is performed. Future hypotheses must be labeled
`hypothesis` and state the exact reproducible evidence rule.

## Deterministic, sanitized outputs

One successful run writes:

- `capture-summary.json`;
- `capture-report.md`;
- `phase-timeline.csv`;
- `transaction-classes.csv`;
- `payload-variability.json`;
- `hashes.sha256`.

Ordering, JSON property names, numeric rounding, CSV line endings, Markdown, and
SHA-256 manifest ordering are stable. The same capture bytes and tool version
produce byte-for-byte identical files.

Default reports may contain the input basename, input/report hashes, timestamps,
sequence numbers, lengths, counts, status values, fingerprints, and variability
statistics. They never contain:

- `write_hex` or `read_hex`;
- raw process handles or pointer values;
- recorder session GUIDs;
- open selectors, controller serial numbers, or machine identifiers;
- input directory paths.

Reports remain evidence and may still disclose timing and structural
fingerprints. Review them before sharing. Keep raw captures, private reports,
local evidence paths, and archives outside Git.

## Tests and synthetic evidence

Tests generate invented JSONL records and include a small invented fixture.
They cover normal startup/configuration/exchange/close, probe-like unmatched
writes, reconnects, redundant closes, unclosed and multiple handles, failed
calls, unexpected reads, queue polling, sequence gaps and duplicates, malformed
JSON, invalid UTF-8, schema/type/hex/count failures, unknown extension fields,
empty input, all six input/report filename collisions with both overwrite
settings, relative and directory-link aliases, legacy temporary filenames, and
a generated 120,002-record streaming capture. Collision tests verify input
bytes and timestamps remain unchanged and that no output is written.

CI builds with warnings as errors, runs all tests, verifies formatting, runs the
CLI against the invented fixture twice, compares every output byte, and checks
that raw synthetic payload sentinels do not appear in reports. CI downloads no
vendor software, capture, firmware, driver, or binary.

## Comparing exactly three sanitized analyses

The offline `compare` command consumes exactly three directories produced by
`analyze`:

```powershell
dotnet run --project tools/MyPlasm.ProtocolAnalyzer -- compare `
  --analysis "C:\PrivateEvidence\run-1\analysis" `
  --analysis "C:\PrivateEvidence\run-2\analysis" `
  --analysis "C:\PrivateEvidence\run-3\analysis" `
  --output "C:\PrivateEvidence\campaign-comparison"
```

Each input directory must contain exactly the six sanitized analyzer outputs
listed above. The command verifies the five reports listed by
`hashes.sha256`, validates the manifest itself and all report schemas, and
rejects missing, extra, malformed, hash-mismatched, ambiguous, or duplicate
sets. It accepts no raw capture, ZIP, payload, native DLL, firmware, or vendor
file. Input directories are never modified.

The output directory must not be the same as, inside, or an ancestor of an
input directory. Normalized aliases and existing symbolic-link or junction
aliases are resolved before reading inputs and again before committing every
output. `--overwrite` replaces only the six known comparison reports and
cannot authorize an evidence collision.

The comparison writes:

- `campaign-summary.json`;
- `campaign-report.md`;
- `stable-transaction-classes.csv`;
- `class-frequency-by-run.csv`;
- `run-structure-comparison.csv`; and
- `hashes.sha256`.

Canonical run labels are assigned after sorting by the SHA-256 of all six
verified sanitized reports, then sanitized capture SHA-256 and record count.
Tied sets are byte-identical. Therefore supplying the same three directories
in any argument order produces byte-for-byte identical reports.

Reports compare structural counts, functions and statuses, phases and
reconnect transitions, sanitized open sessions, deterministic pairs and
anomalies, class presence and frequency, first/last occurrence and phase
overlap, length/fingerprint structure, same-length variability metrics, and
available cadence and latency summaries. A transaction class is labeled
`stable across all three captures` only when the exact sanitized class
fingerprint appears in every run.

Comparison schema version `1` supports analyzer version `1.0.1` and recorder
schema version `1`. Incompatible versions fail closed. Outputs exclude raw
payload fields, recorder session identifiers, selectors and serials, pointer
values, machine identifiers, and absolute local paths.

Use the exact isolated operator procedure in
[differential-capture-campaign.md](differential-capture-campaign.md). No live
three-run campaign has been completed or approved by this implementation.

## Comparing future targeted captures

Preserve every raw capture unchanged and record its SHA-256. Analyze each
capture into a separate empty output directory using its expected hash. Compare
sanitized class fingerprints, frequencies, phases, and timing distributions.
Describe repeated structure as `confirmed`, possible relationships as
`hypothesis`, and unresolved meaning as `unknown`.

Do not replay a capture or classify a command as safe. A structural difference
does not establish causation or semantics.

## Known limitations

- Only recorder schema version `1` and the 11 recorded D2XX functions are
  supported.
- Pairing is FIFO by open handle; it is deterministic but cannot prove
  application-level causality.
- Recorder timestamps and logging overhead limit timing precision.
- A missing final close can reflect process termination or buffered-log loss;
  the analyzer reports the observation without choosing a cause.
- Fingerprints prove byte equality for practical evidence handling but do not
  reveal meaning.
- The tool does not decode framing, checksums, commands, status, coordinates,
  firmware, inputs, or safety.
- Cross-run stability is presence of an exact sanitized fingerprint in three
  verified report sets; it cannot establish causation, meaning, safety, or
  replay suitability.
