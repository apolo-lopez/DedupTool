# Questions & Answers — DedupTool spec review

Recorded during step 0 (code + spec review), 2026-11-06, and updated at the end of implementation (iteration 31, task 9.3). Every question raised in the review is kept here — including resolved ones — so `DECISIONS.md` can reference them later.

Status: ✅ resolved · ⏳ awaiting confirmation · ❓ moved to TECH_DEBT.md · 🛠 implemented and verified in this change

---

## Status at end of implementation

| Item | Answer | Implemented in | Verified by |
|------|--------|----------------|-------------|
| Q1 — dedup conflict | family integrity wins; attachments dedup only within the same parent | 3.2 `CollectAttachmentNonSurvivors` | attachment regression tests; golden e2e (`DOC-000005` emitted) |
| Q2 — cross-mailbox survivor | lowest `docId` survives; losing family suppressed wholesale | 4.2 `CollectEmailNonSurvivors` + 3.4 drag | cross-mailbox tests; golden e2e (family 9 fully out) |
| Q3 — differing attachment sets | losing family suppressed as-is, no merge | 4.2 + 3.4 | cross-family drag tests; **no fixture with asymmetric attachment sets** (accepted risk) |
| Q4 — normalization depth | full formatting normalization, content case preserved | 4.1 `EmailFingerprint` | `EmailFingerprintTests` (8 Q4 fixtures) + `DeduperTests` |
| Q5 — CSV dialect | RFC 4180 quoting, LF endings, verbatim header | 6.1–6.3 `LoadFileWriter` | `LoadFileWriterTests`; Python `csv` round-trip; ingest compatibility still TD-2 |
| Q6 — malformed input | best effort + warnings, exit 0 | 2.3 exclusion, 7.1 warnings, 7.2 input boundary | `ExclusionTests`, `CliTests`; provisional → TD-1 |
| Q7 — debts file name | `TECH_DEBT.md` | — (naming decision) | file exists and is current (9.2) |
| A8 — nested emails | family members, never fingerprint candidates | 4.3 (`ParentId is null` gate) | attached-email tests |

---

## Q1 — Dedup conflict: same-hash attachments under different families ✅

**Spec conflict:** §3.2 + §3.4 say two attachments with the same `sha256` are duplicates, deduped batch-wide. §3.5 + INGEST-4471 say families must arrive intact. For the same file attached to two *different* surviving emails (`DOC-000003` in family `DOC-000001` vs `DOC-000005` in family `DOC-000004`), suppressing either copy breaks the other email's family — and the load-file format has no way to share one row between two parents.

**Answer:** Option 1 — **family integrity wins.**

**Consequences we accept:**
- An attachment is suppressed only when (a) its whole family is suppressed, or (b) it is an identical-sha duplicate *within the same parent*.
- Same-sha attachments under two different surviving families are **both** emitted → reviewers may see identical file content attached to two different emails. Deliberate deviation from §3.2/§3.6; must be written up in `DECISIONS.md`.
- Expected sample output: **12 rows / 4 suppressed** — same totals as the current tool, but a different row set: `DOC-000005` is kept, `DOC-000009`/`DOC-000010`/`DOC-000011` are out. No broken families.

**Alternatives considered:** strict hash dedup (rejected — contradicts the ticket), ask the interviewer (rejected — decided here).

---

## Q2 — Cross-mailbox duplicate email: which copy survives? ✅

**Question:** §3.3 requires recognizing copies of the same email collected from multiple mailboxes, but doesn't say which copy is kept or what happens to the losing copy's family.

**Answer:** Option 1 — deterministic group rule: among a duplicate set of email copies, the **lowest `docId` survives**; the losing copies' **entire families** (email + all descendants) are suppressed.

**Consequences:**
- Order-independent (§6.1): survivor chosen from the group's content, not input order.
- O(n): fingerprint every email, group, keep min `docId`.
- The losing custodian's set simply doesn't contain that family → interpretation of §4: *self-contained* means **no partial families under a custodian**, not *every copy retained*. Goes in `DECISIONS.md`.
- Suppressed count includes every doc of a losing family.

**Alternatives considered:** sender-preferred survivor (more semantic, more rules); one row per custodian (conflicts with §3.6 + DocID uniqueness).

---

## Q3 — Duplicate emails with different attachment sets ✅

**Question:** copies of one email can carry different attachment sets (classic case: a mailbox copy with attachments stripped). Spec is silent; the sample doesn't exercise it.

**Answer:** Option 1 — the losing family is **suppressed as-is**; the surviving copy's attachment set is authoritative.

**Implications (recorded as asked):**
- Attachments that exist *only* on the losing copy disappear from the load file entirely → **potential silent content loss**. No merge, no re-parenting.
- Simple and deterministic; provenance (custodian / parent links) is never rewritten.
- Must be documented in `DECISIONS.md` with a test fixture capturing the behavior.
- Flagged as **our riskiest assumption**: if the interviewer prefers merging, this is the decision that changes the most code.

---

## Q4 — Email fingerprint normalization depth ✅

**Question:** how aggressively to normalize emails for §3.3?

**Risk analysis — the two error types are not symmetric:**
- *False negative* (real duplicate missed) → reviewers see content twice. Visible, recoverable, annoying — a milder version of the status quo the ticket complains about.
- *False positive* (unique email suppressed) → **silent, irrecoverable content loss**. In e-discovery this is the severe, potentially sanctionable failure class — INGEST-4471 is itself a content-loss bug.
- **Conservative** (line-endings + address case + date instant only): lowest false-positive risk, but the spec says *"headers, encodings, and line endings vary between mailboxes"* — subject header folding (extra spaces) and trailing-newline differences are exactly that variance, and it misses them → under-delivers §3.3.
- **Aggressive** (also case-fold subject and body): max recall, but case-folding *content* opens false-positive paths (auto-generated notices/templates differing only in letter case, same second, same recipients) → unacceptable given the asymmetry above.
- **Full** (formatting dimensions only: address case, recipient-set order, subject whitespace collapse, body line-endings + trim, date-as-instant; **content case preserved**): targets precisely the variance classes the spec names, never compares content case-insensitively.

**Evidence run** (scratch script in `/tmp/opencode/normalize_evidence.py`, synthetic fixture pairs — no repo code touched):

| # | fixture | expected | conservative | full | aggressive |
|---|---------|----------|--------------|------|------------|
| 1 | sample pair (address case, LF/CRLF, tz offset) | dup | dup ✓ | dup ✓ | dup ✓ |
| 2 | subject header folding (double space) | dup | keep ✗ | dup ✓ | dup ✓ |
| 3 | recipients listed in different order | dup | keep ✗ | dup ✓ | dup ✓ |
| 4 | body differs ONLY by letter case | keep | keep ✓ | keep ✓ | dup ✗ |
| 5 | subject differs ONLY by letter case | keep | keep ✓ | keep ✓ | dup ✗ |
| 6 | body differs only by trailing newline | dup | keep ✗ | dup ✓ | dup ✓ |
| 7 | same instant, different offset representation | dup | dup ✓ | dup ✓ | dup ✓ |
| 8 | genuinely different recipients | keep | keep ✓ | keep ✓ | keep ✓ |
| | **score** | | **5/8** | **8/8** | **6/8** |

Honest caveats: **the real sample does not discriminate** — all three strategies catch fixture 1. The fixtures encode *plausible* variance classes, not observed data; conservative's errors are false negatives (recoverable), aggressive's are false positives (irrecoverable). Real multi-mailbox batches would be the gold standard — the interviewer promised additional data on the call.

**Answer (user, 2026-11-06):** Option 1 — **full normalization**, ordinal/invariant comparisons everywhere (no culture-sensitive casing), promoted to unit tests in the coding phase. Validation gap to re-check when the interviewer's extra batches arrive.

---

## Q5 — CSV dialect ✅

RFC 4180 quoting (quote fields containing comma/quote/LF, double embedded quotes), LF line endings kept (ingest expectation per current writer). Compatibility with the real ingest job is unverifiable here → tracked as TECH_DEBT.md TD-2.

---

## Q6 — Malformed input handling ❓ (provisional answer shipped; TD-1 provisionally implemented)

**Question:** spec guarantees `parentId` present / valid ISO dates / unique `docId`s, but says nothing about violations (bad `sentDate`, missing parent, duplicate `docId`, empty hash).

**Disposition:** not answerable from the documents → recorded in **TECH_DEBT.md TD-1**. Provisional answer confirmed by user (2026-11-06): **Option 2 — best effort + warnings** (skip invalid docs, warn on stderr, exit 0), never emit a wrong-format date or an orphan row. Still subject to interviewer confirmation (TD-1).

---

## Q7 — Name of the unanswered-questions file ✅

**TECH_DEBT.md** (per the critical rules), not `DEBTS.md`.

---

## Assumptions (not raised as questions — object and we promote them)

- **A1 — dedup classes scoped by `docType`**: loose↔loose by sha (§3.1), attachment↔attachment by sha (§3.2), email↔email by fingerprint (§3.3). An attachment whose hash equals a *loose* file's hash is **not** suppressed (§3.1/§3.2 read as pairwise within type; §3.4 sets the *scope*, not cross-type matching).
- **A2 — deterministic row order**: rows sorted by `DocID` (ordinal) so §6.1's "same load file" holds byte-for-byte, even though "row order is not significant" to ingest.
- **A3 — suppressed count** counts every omitted document, including all descendants of suppressed families.
- **A4 — summary line** stays as-is (already satisfies §6.3): `<batchId>: N documents in, M out, K suppressed as duplicates.`
- **A5 — `FamilyID`/`SentDate` walk to the chain root** (the family's top-level email); a loose file is its own root; `SentDate` empty only for loose files.
- **A6 — no orphan rows**: if a parent is suppressed, all descendants are suppressed with it.
- **A7 — unparseable `sentDate`** (under Q6's best-effort path): skip the doc (and its dependents) with a warning rather than emit a wrong-format date.

## Assumptions discovered while coding (added end of implementation, task 9.3)

These were not visible from the documents alone; each was decided during implementation, recorded in `DECISIONS.md`, and pinned by tests. (A12 and A13 restate A2 and A5 with the implementation detail that settled them — same decisions, now with a named owner in the code.)

- **A8 — nested emails are family members.** An `attachment`-style email (`docType: "email"` **with** a `parentId`) is never a fingerprint candidate: it is suppressed only together with its family and never suppresses a standalone email. Implemented in 4.3 by gating grouping on `ParentId is null`; `DECISIONS.md` decision 10.
- **A9 — exit codes.** 0 = success (skipped-document warnings included), 1 = unusable batch file, 2 = wrong argument count. The spec says only "non-zero" for the failure cases; the split exists so operators can tell an invocation error from an export error. Tasks 7.1–7.3; `DECISIONS.md` decision 12.
- **A10 — warning line format.** `warning: <docId>: <reason>` on stderr, one line per skipped document (invalid doc *and* each skipped descendant), in input order, written before the load file; `(missing docId)` when the doc has no id. The spec requires only "a warning naming the doc". Task 7.1; `DECISIONS.md` decision 11.
- **A11 — attachment dedup key is the literal `parentId`.** Key = `(parentId, sha256)` with no family-root resolution: spec §3.2 names `parentId`, so two identical attachments dedup only under the same immediate parent. Task 3.2; `DECISIONS.md` decision 9.
- **A12 — row order is decided by the writer.** `LoadFileWriter` sorts rows by `DocID` with **ordinal** comparison, so the file is byte-identical for any input permutation regardless of pipeline order. Task 6.3; `DECISIONS.md` decision 13.
- **A13 — family columns come from the resolved root.** `FamilyID` **and** `SentDate` are projections of `ResolveRoot(document)`, so a nested child carries the root email's id and date; loose files are their own root with an empty date. Tasks 5.1/5.3; `DECISIONS.md` decision 14.
- **A14 — duplicate `docId`s make a batch order-sensitive.** First occurrence wins and later ones are skipped (as the spec requires), so the §6.1 any-permutation promise is read as applying to valid batches. Task 2.2/2.3; `DECISIONS.md` decision 15; open as **TD-4**.
- **A15 — skipped documents are not "suppressed".** The input-handling spec overrides design D1's phase-4 counting note: invalid documents and their descendants leave the pipeline before any dedup key or counter. Task 2.3; `DECISIONS.md` decision 8.
- **A16 — fingerprint inputs are content only.** `sha256`, `sizeBytes`, `fileName`, `docId` and `custodian` are excluded from the fingerprint by construction; two copies differing in any of them can still be duplicates. Task 4.1; evidence table in Q4.
