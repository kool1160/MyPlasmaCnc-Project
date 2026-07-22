# Evidence Storage

Use this folder only for small, sanitized, versionable evidence needed by tests or documentation.

Do not commit:

- large installer or runtime archives;
- full diagnostic exports;
- raw packet captures containing unnecessary personal paths or identifiers;
- generated binaries;
- duplicate copies of vendor software.

Preferred test evidence:

- minimal byte fixtures;
- redacted metadata samples;
- deterministic fake-controller transcripts;
- SHA-256 manifests that identify externally stored archives.

Historical files already committed under `Old installed software/` are read-only reference material and should not be duplicated or modified during normal development.
