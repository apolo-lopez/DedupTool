# Tasks

## 1. Test scaffolding (no product code)

- [x] 1.1 Add document factory helpers (email / attachment / loose builders with sensible defaults) to the test project and verify an existing test compiles using them
- [x] 1.2 Add a repo-root path resolver (walks up from the test binary to find `data/batch-sample.json`) and verify a smoke test successfully opens the sample file
- [x] 1.3 Add a deterministic shuffle helper (fixed seed) and verify it returns a true permutation (same elements, different order)

## 2. Validation & family model

- [x] 2.1 Implement the `docId` index, `parentId → children` map, and memoized root resolution with cycle guard; verify unit tests: simple parent resolves to itself's root, nested chain (email → A → B) resolves B to the email, loose file is its own root
- [x] 2.2 Implement the validation pass producing warnings for: unknown parentId, empty sha256, empty/duplicate docId, unparseable or offset-less sentDate, parentId cycles; verify unit tests — one warning per invalid doc naming docId + reason, and a valid batch yields zero warnings
- [x] 2.3 Exclude invalid documents and their descendants from the survivor set (without counting them as suppressed); verify tests: skipped doc's children absent, no emitted row references a skipped docId as ParentID, suppressed count excludes invalid docs

## 3. Class-scoped hash deduplication

- [x] 3.1 Deduplicate loose documents batch-wide by sha256 with the lowest-docId survivor; verify tests: same hash under two custodians → single row with the lowest docId, distinct hashes → both kept, shuffled input → same survivor
- [x] 3.2 Deduplicate attachments by sha256 within the same parent only; verify tests: two identical attachments under one parent → one row; DOC-000003 vs DOC-000005 (same hash, different surviving parents) → BOTH kept (INGEST-4471 regression test)
- [x] 3.3 Keep dedup classes disjoint (attachment hash equal to a loose hash → both kept); verify test
- [x] 3.4 Propagate suppression down the children index (suppressed middle of a chain drags its children); verify orphan test: no emitted row carries a ParentID whose DocID is absent

## 4. Cross-mailbox email fingerprint (§3.3)

- [x] 4.1 Implement fingerprint normalization per design D4 (addresses trim+case-fold, recipients as sorted case-folded list, subject whitespace collapse with case preserved, body → LF + trim with case preserved, sentDate → UTC instant, ordinal comparisons, sha/size/fileName excluded); verify table-driven tests covering all eight `ai/QUESTIONS.md` Q4 fixtures — matching pairs match, case-only body/subject pairs do NOT match, different recipients do NOT match
- [x] 4.2 Group top-level emails by fingerprint and suppress losing families wholesale (email + all descendants) with the lowest-docId survivor; verify tests: sample pair DOC-000001/DOC-000009 → DOC-000001 survives and family 9 (3 docs) is fully suppressed; non-matching emails keep their families
- [x] 4.3 Exclude attached emails (`docType == email` with a parentId) from fingerprint grouping; verify test: attached copy with a matching standalone email is not suppressed by it
- [x] 4.4 Verify determinism at Deduper level: two shuffled runs over the same batch produce identical row sets, identical FamilyID/SentDate values, and identical suppressed counts

## 5. Row projection: FamilyID & SentDate

- [x] 5.1 Set FamilyID from the resolved family root for every row; verify tests: nested chain rows all carry the root email's docId, simple attachment carries its parent email's docId, loose carries its own docId
- [x] 5.2 Implement UTC date formatting exactly `YYYY-MM-DDTHH:MM:SSZ` (offset inputs converted, `Z` passthrough, fractional seconds truncated, offset-less rejected upstream by validation); verify tests for all four cases including `14:22:00-05:00` → `19:22:00Z`
- [x] 5.3 Make every document with a parent carry the family root email's date, and loose files an empty date; verify tests: attachment of email, nested child of attachment (non-empty — the current bug), loose file empty

## 6. Load file writer

- [x] 6.1 Implement `EscapeField` per RFC 4180 (quote when value contains `"`, `,`, CR or LF; double embedded quotes); verify tests: comma in fileName → quoted, quote in fileName → doubled, line feed in field → quoted, ordinary field → returned unchanged
- [x] 6.2 Apply escaping to all eight fields while preserving the exact header and LF row endings; verify header byte-exact test and an LF-only line-ending test
- [x] 6.3 Sort rows by DocID (ordinal) before writing; verify test: writer output over pre-sorted vs reversed row lists is byte-identical

## 7. CLI & reporting (input-handling)

- [x] 7.1 Extend the dedup result with ordered warnings and print each to stderr from `Program` while exiting 0 (use `InternalsVisibleTo` so tests can call `Main`); verify test: batch with an unknown parentId → warning line on stderr naming the doc, exit code 0, load file written
- [x] 7.2 Handle unreadable/malformed batch JSON: error to stderr, non-zero exit, no load file created; verify test
- [x] 7.3 Preserve the usage contract: wrong argument count → usage line on stderr, exit 2; verify test
- [x] 7.4 Keep the success summary format; verify test: sample batch prints exactly `batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.`

## 8. End-to-end integration

- [x] 8.1 Add the golden e2e test over `data/batch-sample.json` asserting the complete expected CSV inline (exact header, 12 data rows sorted by DocID, DOC-000005 present with ParentID DOC-000004, DOC-000009/000010/000011/000013 absent, UTC SentDate on every email/attachment row, empty SentDate on loose rows); verify the test passes against the new implementation
- [x] 8.2 Add the order-independence e2e: shuffle the sample's document array (fixed seed), run the pipeline, assert byte-identical CSV; verify the test passes
- [x] 8.3 Run the full suite via `dotnet test test/DedupTool.Tests/DedupTool.Tests.csproj` and verify all tests pass, including the 3 original starter tests (root `dotnet test` remains broken per TECH_DEBT TD-3)

## 9. Documentation deliverables

- [x] 9.1 Fill in `DECISIONS.md` — every spec ambiguity/contradiction with question/decision/alternatives/why (drawn from `ai/QUESTIONS.md`), what changed, what didn't get to, what's next, AI section; verify each section contains real content, no template dashes left
- [x] 9.2 Update `TECH_DEBT.md` — mark TD-1 as implemented provisionally (best-effort), keep TD-2/TD-3 open with status; verify review
- [x] 9.3 Update `ai/QUESTIONS.md` with any assumptions discovered while coding (A8 nested emails already added; add anything new) and verify statuses are current
- [x] 9.4 Append the iteration entry to `ai/ITERATION_LOG.md` (what was done, decisions, next-task prompt); verify the file has one entry per iteration
- [x] 9.5 Collect the AI conversation transcripts into `ai/` as required by the exercise brief; verify the transcript files exist

## Workflow follow-up

- Run the tool on the sample manually and eyeball `out.csv` against `data/current-tool-output.csv` before calling the change done.
- Archive the change with `openspec archive` after the project's review requirements are satisfied, then verify the archived result.
