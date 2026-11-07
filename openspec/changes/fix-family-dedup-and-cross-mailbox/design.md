# Design

## Context

A single C#/.NET 8 console project (`src/DedupTool`) plus an xUnit test project. Today the pipeline is `Program` → `Deduper.Run(Batch) → DedupResult(rows, suppressed)` → `LoadFileWriter.Write`. Dedup is a single pass with a global `HashSet<sha256>` ("first one we see wins"); `FamilyID`/`SentDate` come from the immediate parent; `SentDate` is passed through raw; the writer does no escaping; everything follows input order.

Constraints that shape this change:

- The CLI (`<batch.json> <loadfile.csv>`) and the load-file header/column order are frozen (SPEC §5, README).
- Batches run up to ~2M documents on one machine; no new dependencies are allowed without discussion.
- Decisions already taken live in `ai/QUESTIONS.md` (family integrity > §3.2, lowest-docId survivor, losing family suppressed as-is, full fingerprint normalization, RFC 4180) and provisional behavior in `TECH_DEBT.md` (TD-1 best-effort input handling).
- See `proposal.md` for motivation; the `specs/` deltas are the behavioral contract.

## Goals / Non-Goals

**Goals:**

- Family-safe, deterministic deduplication incl. cross-mailbox email recognition (O(n log n) or better; single pass over content where possible).
- Spec-exact load file: family-root `FamilyID`, UTC `SentDate`, RFC 4180 quoting, stable row order.
- Testability: pure, individually testable units with an end-to-end golden test on the sample batch.
- Preserve today's structure (small static collaborators, no framework churn).

**Non-Goals:**

- Parallel or streaming processing (JSON is fully materialized today; revisit only if 2M-doc memory becomes real).
- Merging attachments unique to a losing duplicate family (explicitly decided against — `ai/QUESTIONS.md` Q3).
- Changing the CLI, header, or summary-line format.
- Reimplementing in another language.

## Decisions

### D1 — Two-phase pipeline: validate/index → group/suppress → project → sort → write

`Deduper.Run` orchestrates five pure phases:

1. **Validate + index**: build `docId → document`, `parentId → children`, detect invalid docs (missing parent, empty/duplicate `docId`, empty `sha256`, unparseable `sentDate`, cycles) → `warnings`.
2. **Resolve roots**: memoized `root(doc)` walking `parentId` to the top; every doc's `FamilyID` and inherited `SentDate` come from the root.
3. **Group duplicates**: one key per class — loose: `sha256`; attachment: `(parentId, sha256)`; top-level email: fingerprint (D3). Survivor per group = lowest `docId` (ordinal).
4. **Propagate suppression**: non-survivors + invalid docs, then BFS down the children index so suppression drags descendants (no orphans); suppressed docs count here.
5. **Project + sort**: build `LoadFileRow`s for survivors, sort by `docId` ordinal, write.

*Alternative considered:* keeping the current single pass with inline decisions — rejected: family integrity and email grouping require global knowledge anyway, and interleaved mutation is what produced the order-dependence bug.

### D2 — Family model: children index + memoized root walk

`root` is computed with an explicit stack and a visited-set (cycle guard — cycles are caught earlier in validation anyway, so the guard is defense-in-depth). Root gives `FamilyID`, and for any doc with a parent, `SentDate` = root email's date; loose files keep empty.

*Alternative:* recursing from each root to assign descendants — same complexity, but a per-doc memoized walk is simpler to unit-test and naturally handles docs whose root is queried first.

### D3 — Duplicate grouping keys

- Loose: `(docType class "loose", sha256)` — batch-wide (SPEC §3.1, §3.4).
- Attachments: `("attachment", parentId, sha256)` — same-parent only; cross-family same-sha attachments both survive (family integrity outranks §3.2 — `ai/QUESTIONS.md` Q1).
- Top-level emails (`parentId == null`): fingerprint; attached emails (`docType == email` with a parent) are family members only (`ai/QUESTIONS.md` A8).

Groups live in `Dictionary<key, List<Document>>`; exact equality (not hash-only) decides membership, so hash collisions are harmless. Survivor selection per group is `min(docId, StringComparer.Ordinal)` — order-independent by construction (§6.1).

*Alternative:* union-find — unnecessary; keys already partition the documents.

### D4 — Email fingerprint: full formatting-only normalization

Computed once per top-level email, compared ordinally everywhere (`ToLowerInvariant`, `StringComparer.Ordinal` — no culture-sensitive casing):

| field | normalization |
|---|---|
| `from` | trim + case-fold |
| `to` | trim + case-fold each, sort, compare as list |
| `subject` | collapse internal whitespace runs to one space, trim; **case preserved** |
| `body` | `\r\n`/`\r` → `\n`, trim outer whitespace; **case preserved** |
| `sentDate` | parse as ISO 8601 **with offset required**, convert to UTC, format `yyyy-MM-ddTHH:mm:ssZ` |
| `sha256`, `sizeBytes`, `fileName` | excluded (raw bytes/derived names differ between copies) |

Null email fields normalize to empty strings. Evidence for this profile (8/8 vs 5/8 conservative vs 6/8 aggressive) is in `ai/QUESTIONS.md` Q4; the fixtures become unit tests.

**Offset-less timestamps are rejected as invalid** (skip + warn): `DateTimeOffset.TryParse` would otherwise silently interpret them in the machine's local time zone — a nondeterminism trap across machines (§6.1).

### D5 — Result model: `DedupResult` grows warnings

`DedupResult(Rows, Suppressed, Warnings)` where `Warnings` is an ordered list of `(docId, reason)` messages. `Program` prints them to stderr (one line each, before the summary) and keeps exit code 0 — per TD-1. Ordering: input order of detection, so warnings are reproducible for a given input file.

### D6 — CSV escaping in `LoadFileWriter`

A single `EscapeField(string)`: if the value contains `"`, `,`, `\r` or `\n`, wrap in quotes and double embedded quotes; otherwise return unchanged (ordinary rows stay byte-identical to today's output). Applied to all eight fields. LF row endings preserved.

### D7 — Date formatting

One helper `FormatUtc(string isoWithOffset) → string?` (null = invalid). Uses `DateTimeOffset.TryParseExact` against a small format list (`yyyy-MM-ddTHH:mm:ssK`, `yyyy-MM-ddTHH:mm:sszzz`, with/without fractional seconds) with invariant culture, then `ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", InvariantCulture)`. Truncation of fractional seconds falls out of the format string.

### D8 — Test layout

- `DeduperTests` (existing, kept): class-scoped dedup, family integrity, cross-mailbox grouping, survivor rule, orphan propagation, determinism (shuffled input → same result), suppressed counting.
- `EmailFingerprintTests`: the eight Q4 fixtures, table-driven (`[Theory]` + `[InlineData]`).
- `LoadFileWriterTests`: header, quoting, LF endings, row order.
- `SentDateTests` / root-resolution: offset conversion, Z passthrough, fractional truncation, nested chain, loose empty, offset-less rejected.
- `EndToEndTests`: runs the real pipeline over `data/batch-sample.json` (path found by walking up from the test binary to the repo root), asserts the full expected CSV inline plus `16/12/4` summary counts; second run on a shuffled document order asserts byte-identical output.

## Risks / Trade-offs

- **[Fingerprint false negative — duplicate survives]** → normalization table-tested; re-validate against the interviewer's extra batches when available (recorded in `ai/QUESTIONS.md` Q4).
- **[Fingerprint false positive — unique email lost]** → content case is never folded; case-only fixtures assert non-matching; ordinal comparisons only.
- **[Memory at 2M docs]** → JSON + fingerprint strings roughly double today's peak. Mitigation path (not now): group by SHA-256 of the normalized fingerprint while keeping one full copy per group for exact comparison.
- **[RFC 4180 quoting could trip a naive ingest parser]** → only fields that need quoting are quoted, so clean-data output is byte-identical to today; TD-2 tracks confirmation with the ingest team.
- **[Duplicate-`docId` handling is input-order dependent]** → unavoidable when the batch already violates uniqueness; §6.1 applies to batches meeting the input guarantees. Documented, tested as "first occurrence wins".
- **[Suppression BFS on corrupted graphs]** → cycles rejected in validation before grouping; visited-set guards remain.

## Migration Plan

Single tool, no persisted state: deploy = replace the binary. Rollback = `git revert`. The output change for the sample (row set swaps `DOC-000005` in, `DOC-000009/10/11` out; totals stay 12/4) is reviewed by the user before anything is merged; the ingest contract (CLI + header) is untouched.

## Open Questions

Deferred to `TECH_DEBT.md` without affecting this design: TD-1 (interviewer confirmation of best-effort input handling), TD-2 (ingest team's RFC 4180 parsing), TD-3 (`.slnx` vs SDK 8 for `dotnet test`).
