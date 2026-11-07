# Proposal

## Why

INGEST-4471: emails reach review without their attachments. Root cause is a family-blind dedup step — global sha256 "first one wins" suppression removes attachments that belong to *other* surviving families (e.g. `DOC-000005` under `DOC-000004`), violating SPEC §3.5 ("families must be produced intact"). In addition, the spec's cross-mailbox email deduplication (§3.3) has never been implemented, and the load file deviates from §5 in several ways (raw `SentDate` offsets instead of UTC `YYYY-MM-DDTHH:MM:SSZ`, `FamilyID` computed from the immediate parent instead of the family root, no CSV escaping), and the output is not deterministic as §6.1 requires.

## What Changes

- **Family-safe deduplication**: an attachment is suppressed only when its whole family is suppressed (or as an identical-sha duplicate under the same parent); no surviving email ever loses an attachment (fixes INGEST-4471).
- **Cross-mailbox email deduplication (new)**: emails are fingerprinted with full formatting normalization (address case, recipient-set order, subject whitespace, body line-endings/trim, date-as-instant — content case preserved); duplicate copies group together, the lowest `docId` survives, and the losing copies' entire families are suppressed.
- **Deterministic output (§6.1)**: survivor selection independent of input order; rows emitted in a stable order so the same batch always yields byte-identical load files.
- **Load file correctness (§5)**: `SentDate` converted to UTC formatted exactly `YYYY-MM-DDTHH:MM:SSZ` (empty for loose files); `FamilyID`/`SentDate` derived from the family's top-level document by walking the `parentId` chain to its root (fixes nested attachment chains); RFC 4180 quoting for fields containing comma/quote/LF, LF line endings preserved.
- **Input handling (provisional, TECH_DEBT TD-1)**: best-effort validation — invalid documents are skipped with a warning on stderr, exit code stays 0, and no wrong-format date or orphan row is ever emitted.
- **Tests**: unit tests per behavior area plus an end-to-end test asserting the sample batch produces the expected load file, plus adversarial fixtures for fingerprint normalization (ai/QUESTIONS.md §Q4 evidence table).
- **Documentation**: fill in `DECISIONS.md` (spec ambiguities, deviations, gaps) and maintain `TECH_DEBT.md`.

## Capabilities

### New Capabilities

- `deduplication`: what counts as a duplicate (loose/attachment sha256, cross-mailbox email fingerprint), batch-wide scope, family-integrity rule for suppression, deterministic survivor selection, orphan prevention, suppressed counting.
- `load-file`: the CSV contract — fixed header/column order, `FamilyID`/`ParentID`/`SentDate` derivation from family roots, UTC date format, RFC 4180 quoting with LF endings, deterministic row order.
- `input-handling`: best-effort behavior on malformed batches — validation of `docId` uniqueness, parent references, date parsing, hash presence; skip-with-warning semantics; one-line success summary (§6.3).

### Modified Capabilities

<!-- None: this is the first spec in the project (openspec/specs/ is empty). -->

## Impact

- **Code**: `src/DedupTool/Deduper.cs` (core rework), `LoadFileWriter.cs` (quoting), `Models.cs` (if result/warning types are needed), `Program.cs` (warnings on stderr, summary unchanged).
- **Tests**: `test/DedupTool.Tests/` — existing3 tests kept, ~20+ new unit tests, new end-to-end test over `data/batch-sample.json`.
- **Contract**: CLI (`<batch.json> <loadfile.csv>`) and load-file header/column order are preserved exactly; the sample's totals stay `12 out / 4 suppressed`, but the row set changes — `DOC-000005` is kept (family intact) while `DOC-000009`/`DOC-000010`/`DOC-000011` are suppressed (cross-mailbox duplicate family). This is the fix.
- **Docs**: `DECISIONS.md` (required deliverable), `TECH_DEBT.md`, `ai/QUESTIONS.md` (decision record).
