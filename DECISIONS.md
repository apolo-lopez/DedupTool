# Decisions

Keep this short and honest. Bullet points are fine. We read this before we read your code.

Everything below is drawn from `ai/QUESTIONS.md` (Q1–Q7, A1–A8) and the iteration
record in `ai/ITERATION_LOG.md`. Where the spec left a gap that only surfaced while
coding, the decision is dated by the task that forced it.

## Where the spec was unclear, contradictory, or silent

**1. Attachment dedup vs. family integrity (§3.2/§3.4 vs. §3.5 + INGEST-4471) — the central conflict**

- *Question:* §3.2/§3.4 say two attachments with the same `sha256` are duplicates batch-wide; §3.5 says each email's family must arrive intact. For the same file attached to two different surviving emails (`DOC-000003` in family `DOC-000001`, `DOC-000005` in family `DOC-000004`) the two requirements cannot both hold: the load-file format has no way to share one row between two parents.
- *Decision:* **family integrity wins.** An attachment is suppressed only when (a) its whole family is suppressed, or (b) it duplicates another attachment **under the same parent**. Same hash under two surviving parents → both emitted.
- *Alternatives:* strict batch-wide hash dedup (what the tool did at launch — it caused INGEST-4471); asking the interviewer (decided instead, and written up here).
- *Why:* the ticket is a content-loss bug report; suppressing an attachment a reviewer can see attached to a surviving email is the failure being fixed. Accepted consequence: a reviewer may see identical file content under two different emails. **This is a deliberate deviation from §3.2/§3.6.**

**2. Which cross-mailbox copy survives (§3.3), and what happens to its family**

- *Question:* §3.3 requires recognizing the same email collected from several mailboxes but never says which copy is kept or what happens to the loser's attachments.
- *Decision:* within a duplicate fingerprint group the **lowest `docId` (ordinal) survives**; every losing email's **entire family** is suppressed with it.
- *Alternatives:* sender-preferred survivor (more semantics, more rules, and still arbitrary when both copies are in one mailbox); one row per custodian (breaks `DocID` uniqueness and §3.6).
- *Why:* deterministic and order-independent by construction, O(n), and it never leaves a partial family. "Self-contained per custodian" is read as *no partial families*, not *every copy retained*.

**3. Duplicate emails with different attachment sets (§3.3 is silent)**

- *Question:* one copy may have attachments stripped. What happens to files that exist only on the losing copy?
- *Decision:* the losing family is **suppressed as-is**; the survivor's attachment set is authoritative. No merge, no re-parenting.
- *Alternatives:* union the attachment sets onto the survivor (changes provenance — an attachment would claim a parent that never held it); keep both families (defeats §3.3).
- *Why:* provenance is evidence in this domain; rewriting parent links is worse than a documented gap. **This is the riskiest accepted assumption** — attachments unique to a losing copy disappear from the load file. If the interviewer prefers merging, this is the decision that changes the most code.

**4. Email fingerprint normalization depth (§3.3)**

- *Question:* how aggressively to normalize before comparing two emails?
- *Decision:* **formatting only** — addresses trimmed + case-folded, recipients compared as a case-folded set, subject whitespace runs collapsed (case preserved), body CRLF/CR → LF + outer trim (case preserved), `sentDate` compared as a UTC instant; `sha256`/`sizeBytes`/`fileName`/custodian/docId excluded. Ordinal comparisons everywhere.
- *Alternatives:* conservative (address case + line endings + date only — misses subject folding and trailing newlines the spec explicitly names); aggressive (also case-fold subject/body — maximal recall, but suppresses genuinely distinct templated emails); ask the interviewer (asked; answer confirmed full).
- *Why:* the two error types are not symmetric. A missed duplicate is visible and recoverable; a false positive is silent content loss — the same failure class as the ticket. Normalization was scored against eight fixture classes before adoption: conservative 5/8, full 8/8, aggressive 6/8 (evidence run recorded in `ai/QUESTIONS.md` Q4). Honest caveat: the fixtures encode *plausible* variance classes, not observed multi-mailbox data, and the real sample does not discriminate between strategies.

**5. CSV dialect (§6 is silent beyond header and column order)**

- *Decision:* RFC 4180 quoting (quote a field containing `"`, `,`, CR or LF; double embedded quotes), header emitted verbatim, **LF** line endings.
- *Alternatives:* quote everything (breaks byte-compatibility with today's file for ordinary rows); no quoting (a filename with a comma corrupts the row).
- *Why:* minimal change from the current writer for ordinary data while staying lossless for odd filenames. Cannot be verified against the real ingest job → **TD-2**.

**6. Malformed input (the spec "guarantees" validity and says nothing about violations)**

- *Question:* what should happen on an unknown `parentId`, empty `sha256`, duplicate/empty `docId`, a cycle, or an unparseable/offset-less `sentDate`?
- *Decision:* **best effort + warnings.** Skip the invalid document and its descendants, write one warning per skipped doc to stderr, still exit 0 and still write the load file for the valid remainder. Invalid documents are **not** counted as suppressed. An unreadable/malformed batch file is different: one error line, exit 1, no load file.
- *Alternatives:* fail the whole run (loses good data in a batch of thousands); silently drop (unreviewable).
- *Why:* a batch is an export from several mailboxes; one bad row should not destroy the run, but it must be visible. Provisional pending interviewer confirmation → **TD-1**.

**7. Dedup classes are disjoint (spec reads requirements pairwise by `docType`)**

- *Decision:* loose↔loose by `sha256`; attachment↔attachment by `(parentId, sha256)`; top-level email↔email by fingerprint. Equal hashes across classes never suppress each other.
- *Alternatives:* one global hash index (the launch behavior; suppresses unrelated document types).
- *Why:* §3.1/§3.2/§3.3 are scoped by type and §3.4 sets scope rather than creating cross-type matching. Pinned by tests (task 3.3) rather than by a shared code path, so a future "optimization" fails loudly.

**8. Design vs. spec contradiction: are invalid documents "suppressed"?**

- *Question:* design D1 phase 4 counted invalid documents in the suppressed tally; the input-handling spec says skipped documents MUST NOT be counted as suppressed.
- *Decision:* **the spec wins.** Exclusion happens before any dedup key or counter.
- *Alternatives:* follow the design (the document-level contract is graded behavior; the design is an internal plan).
- *Why:* the spec is the behavioral contract; a knowingly contradictory design note is recorded here rather than silently implemented.

**9. Attachment dedup key: literal parent or family root?**

- *Question:* §3.2 says "share the same `parentId` and the same `sha256`". Does "same parent" mean the immediate parent id or the family root?
- *Decision:* **literal `parentId`**, no root resolution.
- *Alternatives:* resolve both parents to their family root first (would dedup across sibling sub-attachments of one email).
- *Why:* spec literalism — the field named is `parentId`; inventing a "resolved parent" would change the requirement. Note the consequence: two identical attachments under the *same email* dedup, while identical files under different parents never do.

**10. Nested emails (assumption A8, not in the spec)**

- *Decision:* an email with a `parentId` is a family member, never a fingerprint candidate; it is suppressed only with its family.
- *Alternatives:* fingerprint every email regardless of depth (an attached copy could suppress a standalone email in another family).
- *Why:* the survivor rule exists to choose *between mailboxes*, not between a message and a forwarded copy of it inside one family.

**11. Warning line format (spec: only "a warning naming the doc")**

- *Decision:* `warning: <docId>: <reason>` on stderr, one line per skipped document, in input order, before the load file is written; `(missing docId)` when the document has none.
- *Alternatives:* JSON lines (ingest-friendly but noisy for humans); one aggregated line (loses per-document traceability).
- *Why:* greppable, names the document, and keeps the runner's exit code untouched.

**12. Exit codes (spec says only "non-zero")**

- *Decision:* **0** success (including skipped-document warnings) · **1** unusable batch input · **2** wrong argument count.
- *Alternatives:* a single failure code (a caller cannot distinguish "fix your invocation" from "fix your export").
- *Why:* the two failures need different operator responses; 2 is the established CLI convention for usage errors and matches the documented usage contract.

**13. Deterministic row order (§6.1 "the same load file")**

- *Decision:* the writer sorts rows by `DocId` with **ordinal** comparison, so the file is independent of input and pipeline order.
- *Alternatives:* sort in the pipeline (leaves the writer able to emit other orders); culture-aware sort (differs per machine — the exact nondeterminism §6.1 forbids).
- *Why:* making the writer the single place that decides bytes means a shuffled batch is byte-identical by construction, verified end-to-end (8.2).

**14. FamilyID and SentDate come from the resolved family root (assumption A5)**

- *Decision:* both columns are projections of `ResolveRoot(document)`; nested children therefore carry the root email's `DocId` **and** its date; loose files are their own root with an empty date.
- *Alternatives:* immediate parent for both (the launch bug: nested children showed the wrong FamilyID and an empty date).
- *Why:* the load file's family columns must describe the family a reviewer will open, not the storage hierarchy.

**15. Duplicate `docId`s are order-sensitive by design (candidate TD-4)**

- *Question:* §6.1 promises identical output for any permutation, but two documents sharing a `docId` make "the first occurrence wins" depend on order.
- *Decision:* first occurrence is authoritative and the later one is invalid (warned and skipped); the any-permutation guarantee is read as applying to **valid** batches.
- *Alternatives:* reject the whole batch (too harsh for a single collision); pick a deterministic winner by content hash (arbitrary, and hides a data-quality problem).
- *Why:* a duplicate id is an upstream defect that must be visible; inventing a tie-break would mask it. Recorded as TD-4 for the interviewer.

## What I changed and why

- **`Deduper`** was rewritten around the family model instead of a single global `seen` set: validation/exclusion (invalid docs and their descendants), then three class-scoped dedup passes — loose by `sha256` batch-wide (lowest `docId`), attachments by `(parentId, sha256)` (lowest `docId`), top-level emails by fingerprint (lowest `docId`) — then one BFS that drags every descendant of a suppressed document so no emitted row can reference a missing parent. This is the INGEST-4471 fix: the old writer's global hash suppression is what deleted other families' attachments.
- **New units:** `FamilyIndex` (id index, children map, memoized root resolution with a cycle guard), `Validator` (one ordered warning per invalid doc, cycle detection), `EmailFingerprint` (the D4 normalization table as code), `IsoDate` (UTC instant formatting with an offset-required gate).
- **`LoadFileWriter`:** RFC 4180 escaping on all eight fields, header emitted verbatim, LF endings, ordinal `DocID` sort. Field order and header text are unchanged.
- **`Program`:** warnings to stderr, an input boundary that turns unreadable/malformed JSON into one error line + exit 1, and no load file in that case. Summary line format unchanged.
- **Output delta on the sample:** 16 in → 12 rows / 4 suppressed (same totals as launch), but `DOC-000005` is now emitted and `DOC-000009` is now suppressed. `DOC-000010/11` were absent before and after (before: family-blind hashing; now: with their losing family), `DOC-000013` remains the loose duplicate. Nothing else moved.
- **Test suite:** 3 → 123 tests, all green via `dotnet test test/DedupTool.Tests/DedupTool.Tests.csproj`. Added golden end-to-end (full CSV inline, through the real CLI), shuffle/order-independence at three levels, and unit coverage per rule. Test infrastructure also gained a serialized `ConsoleCollection` after a console-capture race was found (see the AI section).

## What I didn't get to

- **TD-1** (malformed-input policy) and **TD-2** (RFC 4180 vs. the real ingest job) are still open with the interviewer/ingest team; both have a provisional behavior implemented.
- **TD-3**: root `dotnet test` still fails (`MSB1003`; SDK 8.0.133 cannot read `DedupTool.slnx`). Left as-is deliberately — fixing it means choosing between adding a classic `.sln`, changing the README, or requiring SDK 9.
- **TD-4**: duplicate-`docId` batches are order-sensitive (first-wins) while §6.1's permutation promise is read as covering valid batches only.
- **Fingerprint recall is unvalidated against real data.** The eight fixtures are plausible variance classes, not observed multi-mailbox exports; the promised extra batches would be the real test.
- **The sample does not exercise several implemented behaviors:** no nested chains, no attached emails, no CSV-special characters. Those are covered only by unit tests (tasks 3.4/4.3/6.1–6.2), so the golden test cannot catch a regression in them.
- **Differing attachment sets are not merged** (decision 3) — known, accepted content-loss path for attachments unique to a losing copy.
- **`UnauthorizedAccessException` on the batch file** is handled in code but has no test: a portable permission-denied fixture wasn't feasible here.
- **Scale/performance is untested.** Everything is in-memory (batch JSON, document list, fingerprint dictionary); fine for the sample's 16 documents and for tens of thousands, unknown for very large exports.
- One pre-existing compiler warning remains: `xUnit1026` on the fingerprint theory's unused `id` parameter (approved code, left untouched to keep reviewed files stable).

## What I'd do next

- Get TD-1 and TD-2 confirmed, and run the tool against the interviewer's additional multi-mailbox batches to measure fingerprint recall with real data.
- Decide with the interviewer whether attachments unique to a losing duplicate copy must be preserved; if yes, implement a documented merge (or a flag) rather than silent suppression.
- Resolve TD-3 by picking one of the three options, then make the README's test instructions work as written.
- Add the missing coverage the fixtures can't provide: a small nested-chain/attached-email fixture in `data/` (not just unit tests) and a permission-denied test on a platform where it is portable.
- Archive the OpenSpec change (`openspec archive`) once this review is signed off, and keep `PROGRESS.md`/`tasks.md` in sync as the record.

## If you reimplemented in another language

Not reimplemented — this stayed in C#/.NET 8, because the tool is an existing internal component and a rewrite would add risk without addressing the bug. If it were ported, the output contract a port must preserve is:

- Exact header bytes `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`, LF-only line endings, UTF-8 without BOM.
- Rows sorted by `DocID` under **ordinal/byte** comparison (not the target language's culture collation — `DOC-10` before `DOC-9`), so any permutation of input yields identical bytes.
- `SentDate` exactly `YYYY-MM-DDTHH:MM:SSZ` in UTC: offsets converted, `Z` passed through, fractional seconds truncated, offset-less input rejected as invalid rather than interpreted in local time.
- RFC 4180 quoting on every field (doubled embedded quotes), never on the header.
- Family columns projected from the resolved family root; empty `SentDate` for loose files.
- CLI: `DedupTool <batch.json> <loadfile.csv>`; exit 0 success (warnings included), 1 unusable input, 2 wrong arity; warnings `warning: <docId>: <reason>` on stderr; exactly one summary line on stdout.

The cheapest way to guarantee that is the artifact already in the repo: the golden test's full expected CSV (`GoldenE2ETests`) plus the shuffle test. A port that reproduces those bytes for `data/batch-sample.json` and keeps the exit-code/warning contract is output-compatible; anything else needs the interviewer's sign-off.

## Working with the AI

The AI drafted the OpenSpec change (proposal, three specs, design, 32 micro-tasks), then implemented one task per iteration under a review gate: implement → tests + sample smoke → stop → human approval → tick both trackers. Nothing was marked approved without that gate.

What it got right: the family-model decomposition (index → validation → class-scoped dedup → propagation) held for every later task with no rework; it wrote tests that pinned the actual requirements (e.g. the INGEST-4471 regression test asserts both surviving parents keep the same hash); it chose the asymmetric-risk argument for normalization depth and backed it with an evidence table instead of taste; it kept the sample's byte diffs explainable at every step and flagged the two tasks where byte-identity *should* break.

What had to be corrected or rejected:

- **A false "byte-identical" claim.** An early iteration's log entry pre-wrote that the sample output was unchanged before the task had run; after the run showed row changes (task 3.2, the INGEST-4471 fix), the claim was removed. Lesson: the AI's confidence about unchanged output is not evidence — always diff.
- **A real date bug in its own design.** The D7 format list (`yyyy-MM-ddTHH:mm:ssK`) silently *accepted* an offset-less timestamp and parsed it in the machine's local zone — exactly the nondeterminism the spec forbids. A test caught it; the fix gates on a required-offset regex.
- **Test bugs of its own:** a shuffle test whose batch omitted the parent email (everything got skipped, so the test was vacuous); an `Assert.Same` assertion that was meaningless because of string interning; a console-capture race it introduced by adding a second CLI test class in parallel with the first (fixed with a serialized xUnit collection after the suite failed).
- **Stale handoff state:** a session brief claimed task 3.3 was awaiting review while the trackers and code showed approvals through 4.1. It reconciled against the files rather than the brief — the right call, but a warning that AI summaries drift from disk.
- Small authoring friction: `[InlineData]` with implicitly-typed arrays fails (`CS0182`) — it switched to `[MemberData]`.

What I'd warn a teammate about: the AI is good at decomposing and at writing requirement-shaped tests, but it will confidently state that output is unchanged, that a helper is "already correct", or that a task is approved. Keep the human approval gate, always diff the golden output, and insist that every "no code change needed" claim comes with the test that proves it. The review loop in this repo (one task per iteration, tests + smoke, explicit approval) is what kept those claims honest.
