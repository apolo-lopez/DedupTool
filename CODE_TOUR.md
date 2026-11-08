# DedupTool — a guided tour of the code

**Last updated:** 2026-11-07 · covers commit `97b7195` (branch `fix/family-dedup-and-cross-mailbox`).

This is the "walk me through it" document: what each file does, how a batch flows through the
pipeline, why the original tool dropped attachments (INGEST-4471), and — the part worth
re-reading — **which changes are safe and which ones quietly break family integrity,
determinism, or the ingest contract**.

Companion documents, in increasing order of detail: `SPEC.md` (what the product must do),
`DECISIONS.md` (every ambiguity and the call made), `ai/QUESTIONS.md` (Q/A trace),
`TECH_DEBT.md` (unresolved questions), `openspec/changes/fix-family-dedup-and-cross-mailbox/`
(specs + design + task list).

---

## 1. The contract in one page

```
DedupTool <batch.json> <loadfile.csv>
```

| Aspect | Behaviour |
|---|---|
| Arguments | Exactly 2. Anything else → `usage: DedupTool <batch.json> <loadfile.csv>` on stderr, **exit 2** |
| Batch unreadable / not JSON | `error: cannot read batch "<path>": <message>` on stderr, **exit 1**, no load file written (an existing file at the output path is left untouched) |
| Invalid documents *inside* a valid batch | `warning: <docId>: <reason>` on stderr, document skipped, **exit 0**, load file written for the rest |
| Success | `warning:` lines first (input order), then the load file, then one summary line on stdout: `<batchId>: N documents in, M out, K suppressed as duplicates.` |
| Exit 0 | Run completed. Warnings are *not* failures |
| Exit 1 | Input unusable — nothing processed |
| Exit 2 | Wrong number of arguments |

Input: `{ "batchId": string, "documents": Document[] }` with `docId`, `custodian`, `docType`
(`email` | `attachment` | `loose`), `parentId`, `fileName`, `sha256`, `sizeBytes`, and for emails
`sentDate` (ISO 8601 **with** offset), `from`, `to[]`, `subject`, `body`.

Output: fixed header, eight columns, LF endings, UTF-8 without BOM:

```
DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash
```

Rules that are *not* negotiable because the ingest job breaks: header text, column order, and
`SentDate` as `YYYY-MM-DDTHH:MM:SSZ`. Everything else (row order, escaping) is ours to decide,
and we decided: rows sorted by ordinal `DocID`, fields RFC 4180-escaped only when they need it.

---

## 2. Code map

| File | Lines | Role |
|---|---:|---|
| `src/DedupTool/Program.cs` | 49 | CLI boundary: arity check, file/JSON errors, warnings to stderr, summary, exit codes |
| `src/DedupTool/Models.cs` | 49 | `Batch`, `Document`, `LoadFileRow`, `ValidationWarning`, `ValidationIssue`, `DedupResult` |
| `src/DedupTool/Validator.cs` | 188 | Phase 1 — finds invalid documents, one warning each, input order; cycle detection with memoisation |
| `src/DedupTool/FamilyIndex.cs` | 88 | `docId` index, `parentId → children` map, memoised family-root resolution, cycle guard |
| `src/DedupTool/Deduper.cs` | 455 | The pipeline: skip pass → three dedup classes → descendant drag → row projection |
| `src/DedupTool/EmailFingerprint.cs` | 94 | §3.3 fingerprint: formatting-only normalization of five email fields |
| `src/DedupTool/IsoDate.cs` | 50 | Offset-required ISO parsing → exact UTC `yyyy-MM-ddTHH:mm:ssZ` |
| `src/DedupTool/LoadFileWriter.cs` | 64 | Header constant, RFC 4180 escaping, ordinal row sort, LF, single write |
| `src/DedupTool/Properties/AssemblyInfo.cs` | 6 | `InternalsVisibleTo("DedupTool.Tests")` |

Total product code: ~1,000 lines. Tests: ~2,400 lines across 13 files, **123 tests**.

---

## 3. The pipeline, end to end

`Deduper.Run(batch)` is a straight line — every stage is pure, no I/O, no clocks, no randomness:

```
Input batch (documents, in file order)
        │
   ┌────▼─────────────────────────────────────────────────────────────┐
   │ 1. VALIDATE      Validator.Analyze → issues (index + reason)     │
   │ 2. INDEX         FamilyIndex: docId → doc, parentId → children[]  │
   │ 3. SKIP          invalid docs + all descendants → skipped set     │
   │    + warnings    one per skipped doc, input order                 │
   │ 4. DEDUP GROUPS  three independent scans over surviving docs:     │
   │                  loose      : sha256, batch-wide                  │
   │                  attachment : (parentId, sha256), same parent     │
   │                  email      : fingerprint, top-level only         │
   │ 5. DRAG          descendants of every non-survivor → suppressed   │
   │ 6. PROJECT       one row per survivor; FamilyID/SentDate from the │
   │                  resolved family root                             │
   └────┬─────────────────────────────────────────────────────────────┘
        │
   DedupResult(rows in input order, suppressed count, warnings)
        │
   Program: warnings → stderr · LoadFileWriter: sort + escape → file · summary → stdout
```

The two counting buckets are deliberately different:

- **skipped** (invalid input) → no row, **not** counted as suppressed, one warning each;
- **suppressed** (duplicates, including descendants dragged down by a duplicate) → no row, counted.

So `documents in = rows out + suppressed + skipped`, and the summary only shows the first three.
That is why a batch of 7 with one invalid document can print `7 in, 4 out, 2 suppressed`.

---

## 4. Module by module

### 4.1 `Program.cs` — the boundary

Four responsibilities, nothing else: arity (exit 2), deserialization with a narrow catch
(`IOException`, `UnauthorizedAccessException`, `JsonException` → exit 1, no stack traces, no
truncated output file), warnings to stderr, summary to stdout. Case-insensitive property binding
means `docId`/`DocId` both parse. A JSON `null` body is treated as an error
(`file contains no JSON object`), not as an empty batch.

The failure ordering matters: the batch is fully read and parsed *before* the writer is reached,
so a failed run cannot destroy a previous load file at the output path.

### 4.2 `Validator.cs` — what counts as invalid

Checks run per document, **first failure wins**, in this order:

1. `empty docId`
2. `duplicate docId` (any earlier non-empty id counts, even if that earlier doc is itself invalid)
3. `unknown parentId "<id>"` — presence only, not validity of the parent
4. `parentId cycle`
5. `empty sha256`
6. for `docType == "email"` only: `missing sentDate` / `sentDate not ISO 8601 with offset: "<value>"`

Two deliberate decisions here:

- **Offset-less timestamps are rejected.** `DateTimeOffset.TryParse("2024-03-05T14:22:00")` would
  assume the *machine's* local zone — the same batch would produce different bytes on a laptop in
  Berlin and a server in Virginia. The regex requires `Z` or `±hh:mm` (design D4/D7, §6.1).
- **Cycles are detected before grouping**, with a path-memo so a deep chain costs O(n) overall,
  not O(n²). The doc itself need not be in the cycle: any ancestor loop makes it invalid.

Validation only *reports*; exclusion happens later, so a document can be invalid while its parent
is perfectly fine.

### 4.3 `FamilyIndex.cs` — the family model

Built once per run, three structures:

- `_byId`: `docId → Document`, **first occurrence wins** on duplicates;
- `_children`: `parentId → Document[]` (input order preserved);
- `_rootCache`: memoised root per `docId`.

`ResolveRoot` walks `parentId` upward, caching **every node on the path**, so a 100k-deep chain
resolves in linear total work. It stops at: no parent, unknown parent (validation already warned),
or a revisited id (cycle guard — nothing is cached in that case, so a cyclic chain never poisons
the cache for a valid document).

This is what makes "family" mean "the top-level email", not "my immediate parent". Nested
structure — email → attached email → PDF — resolves the PDF to the *outer* email, and every row
in that tree reports the same `FamilyID` and the same `SentDate`.

### 4.4 `Deduper.cs` — the five passes

**Pass A — `CollectSkipped`.** Seeds from every invalid document, then BFS down `_children`. Only
*first occurrences* seed the BFS: children address their parent by `docId`, which resolves
first-wins, so a duplicate second occurrence must not drag the first occurrence's children along.
`HashSet<Document>` is reference-keyed (no equality overrides) — two docs sharing a `docId` stay
distinct entries.

**Pass B — `CollectLooseNonSurvivors`.** Groups `docType == "loose"` by `sha256` across the whole
batch; lowest `docId` (ordinal) survives. Skipped documents never enter a group, so an invalid
copy can neither win nor cause suppression.

**Pass C — `CollectAttachmentNonSurvivors`.** Groups `docType == "attachment"` by the **literal
pair** `(parentId, sha256)`; lowest `docId` survives. This single line is the INGEST-4471 fix: two
identical PDFs under two *different* parents are not duplicates of each other, because suppressing
one would leave the other email short an attachment (§3.5).

**Pass D — `CollectEmailNonSurvivors`.** Groups `docType == "email"` **with `parentId == null`**
by `EmailFingerprint`; lowest `docId` survives. Attached emails (an email inside a family) are
family members and never group — otherwise one family would lose a member to another family, or
an email would be suppressed while its parent survives. `null` fingerprints are skipped
defensively (validation already excludes them).

**Pass E — `CollectDraggedDescendants`.** BFS from every non-survivor down `_children`; each child
is dragged only if its *resolved* parent is exactly the doomed document. Consequences:

- a losing family collapses **wholesale** — the email and every attachment, at any depth;
- no row ever references a `ParentID` that is not itself emitted (no orphan rows, ever);
- dragged documents count as **suppressed**, unlike skipped ones;
- a doomed document never drags a child that belongs to a same-`docId` sibling.

**Projection.** `FamilyID` and `SentDate` both come from `index.ResolveRoot(d)` — never from the
immediate parent. The three normal cases fall out uniformly: top-level email → itself; attachment
→ its root email's id and date; loose file → itself, empty date (loose docs have no `sentDate`).
`SentDate` is formatted through `IsoDate.FormatUtc`; an unformattable date yields an empty cell
rather than a raw non-UTC string.

### 4.5 `EmailFingerprint.cs` — "same email" without raw bytes

A record of five normalized fields; equality is default record equality over ordinal strings.

| Field | Normalization | Why |
|---|---|---|
| `From` | trim + lowercase | same address, different header casing/whitespace per mailbox |
| `To` | each trimmed + lowercased, **sorted**, joined by `\n` | recipient order is not meaningful |
| `Subject` | whitespace runs → one space, trimmed, **case preserved** | header folding inserts tabs/newlines |
| `Body` | CRLF/CR → LF, outer whitespace trimmed, **case preserved** | mailbox-dependent line endings |
| `SentDate` | UTC instant, seconds precision | `-05:00` and `Z` copies of the same moment must match |

Deliberately **excluded**: `sha256`, `sizeBytes`, `fileName`, `custodian`, `docId` — those differ
between copies by definition. Content case is *never* folded: two emails differing only in body
case are different emails, because suppressing one would lose information irrecoverably. This is
decision 4 (Q4, "full" profile, 8/8 evidence fixtures).

### 4.6 `IsoDate.cs` — one date format, one zone

Regex-gated (offset required), parsed with `InvariantCulture`, converted with `ToUniversalTime()`,
formatted `yyyy-MM-ddTHH:mm:ss'Z'`. Fractional seconds are accepted on input and truncated on
output (the load-file format has no fraction). Same gate as the validator, so "valid" and
"formattable" cannot disagree.

### 4.7 `LoadFileWriter.cs` — the ingest contract

- Header emitted verbatim from a `const` (escaping it would corrupt the separators).
- `EscapeField`: quote only when the value contains `"`, `,`, CR or LF; embedded quotes doubled.
  Ordinary values keep their exact bytes.
- Rows sorted by `DocID` with **ordinal** comparison — stable, culture-free, platform-free.
  `DOC-10` sorts before `DOC-9`; that is intentional and is *the* reason shuffled input produces
  identical bytes.
- LF endings on every line, single `File.WriteAllText` (UTF-8, no BOM).

---

## 5. Worked example — `data/batch-sample.json`

16 documents, two custodians, three intersecting stories. `jsmith` has the original Q3 email (001)
with two attachments (002, 003) and a forwarded copy (004) carrying the same `vendor-report.pdf`
(005). `mlee` has a second copy of the Q3 email (009) with the same two attachments (010, 011), a
reply (012), the same `policy.docx` as a loose file (013), and an unrelated email (015) with an
attachment (016).

| Doc | Type | Verdict | Why |
|---|---|---|---|
| 001 | email | **kept** | family root; fingerprint group member, lowest docId of {001, 009} |
| 002 | attachment | **kept** | `(001, d0687f…)` unique in its group |
| 003 | attachment | **kept** | `(001, 812ffd…)` — different parent from 005, so not a duplicate |
| 004 | email | **kept** | fingerprint differs (subject/body/recipients) |
| 005 | attachment | **kept** | **the INGEST-4471 case**: same hash as 003, different surviving parent |
| 006 | email | **kept** | fingerprint unique |
| 007 | loose | **kept** | unique hash |
| 008 | loose | **kept** | lowest docId of {008, 013} |
| 009 | email | **suppressed** | same fingerprint as 001 (offset `-05:00` ≡ `Z`, CRLF body, `John.Smith@` casing) |
| 010 | attachment | **suppressed** | dragged down with family 009 |
| 011 | attachment | **suppressed** | dragged down with family 009 |
| 012 | email | **kept** | a reply — different subject, body, sender, time |
| 013 | loose | **suppressed** | same sha256 as 008 |
| 014 | loose | **kept** | unique hash |
| 015 | email | **kept** | unique fingerprint |
| 016 | attachment | **kept** | `(015, f174b5…)` unique |

**16 in → 12 out, 4 suppressed** (009, 010, 011, 013), exit 0, no warnings.

The output differs from `data/current-tool-output.csv` in exactly two rows plus one column-wide
format change:

- `DOC-000005` **added** — the attachment the old tool wrongly swallowed;
- `DOC-000009` **removed** — the cross-mailbox duplicate (§3.3);
- every `SentDate` rewritten from `-05:00` offsets to UTC `Z` (§5).

Note what did *not* happen: `003` and `005` both survive with identical hash and name, so the
same PDF appears twice in the load file. That is correct and intentional — but it is the first
thing to understand before changing any dedup key (see §7.1).

---

## 6. Why the original tool broke (INGEST-4471)

The launch version treated `sha256` as globally unique and kept the first occurrence it saw,
ignoring `docType` and `parentId` (and input order, via no sort). In the sample that meant:

```
003 vendor-report.pdf (parent 001)  ──┐
005 vendor-report.pdf (parent 004)  ──┤ same hash → "first wins" suppresses 005
010 Q3-budget.xlsx   (parent 009)  ──┤ same hash as 002 → suppressed
011 vendor-report.pdf(parent 009)  ──┘ same hash as 003 → suppressed
```

Email 004 arrived in review with **no attachment at all**; email 009 likewise. The fix is not a
tweak to the same loop — it is the realisation that "duplicate" is **per class**:

| Class | Key | Scope |
|---|---|---|
| loose | `sha256` | entire batch |
| attachment | `(parentId, sha256)` | one parent, i.e. one family |
| email | fingerprint of 5 fields | entire batch, top-level only |

Everything else in this codebase — family root resolution, descendant dragging, fingerprinting,
deterministic writing — exists to make those three definitions safe to apply together.

---

## 7. Use cases and change sensitivity

This is the section to consult before altering the tool. Each row states current behaviour and what
would change if the underlying rule moved.

### 7.1 Family structure

| Scenario | Current behaviour | If you change the rule |
|---|---|---|
| **Same PDF attached to two different emails** | Kept twice (sample 003 + 005) | Reverting to batch-wide attachment hashing re-creates INGEST-4471: one family loses its attachment. Never do this without a spec change |
| **Same PDF attached twice to the same email** | One survives — lowest `docId`; the survivor's `fileName` is what ships | Switching the survivor rule changes *which filename* reviewers see (cosmetic but visible). Switching to "keep both" leaves duplicate rows inside one family |
| **Nested chain: email → attached email → PDF** | `FamilyID`/`SentDate` come from the outer email; the attached email is never fingerprint-grouped | Keying attachments by "family root id + hash" instead of literal `parentId` would start suppressing identical files across depths — narrower families, more risk. Decision 9 |
| **Moving a document to another parent** | Row's `FamilyID`/`SentDate` change; its dedup group changes (may now collide with siblings, or stop colliding); if the new parent is suppressed, it is dragged too | This is the cheapest way to reason about "what does a family change do": re-parenting can silently *remove* a row from output if it lands in a losing family |
| **Suppressing a root** | Whole subtree disappears: attachments, nested emails, their attachments. All count as suppressed | Keeping subtree members while dropping the root would emit orphan rows — explicitly forbidden by the "no dangling ParentID" invariant |
| **Self-parent or A→B→A loop** | Provider: `parentId cycle` warning; both docs skipped with descendants, exit 0 | Making cycles fatal is a policy change (TD-1 family) |
| **Attachment with `parentId: null`** | Passes validation; groups with *other parentless attachments* by `(null, sha256)`; `FamilyID` = itself; empty `SentDate` | Spec says attachments always have a parent; this is unvalidated malformed input (see §9). A validator rule `attachment ⇒ parentId required` would move these to warnings |
| **`parentId: ""` (empty string)** | Treated as a parent id: `unknown parentId ""`, doc skipped (verified by probe) | Normalising `""` to `null` would silently change meaning — prefer the warning |
| **Email attached to an email, identical in two families** | Both kept — attached emails never group | Grouping them would violate family integrity (one family keeps the email, the other loses a member) |

### 7.2 Email identity (fingerprint)

| Scenario | Current behaviour | If you change the rule |
|---|---|---|
| **Same email, different mailbox offsets** (`14:22-05:00` vs `19:22Z`) | Duplicates — compared as UTC instants | Comparing raw strings would break the single most common real-world case |
| **Same email, different `To` order** | Duplicates — recipients sorted | Order-sensitive comparison would miss copies Bcc'd/ordered differently |
| **Same email, `Re:` / `FW:` / `AW:` prefix added** | **Not** duplicates — subject compared as text after whitespace collapse | Stripping prefixes would catch occasional duplicates *and* merge genuine replies into originals — the risky direction |
| **Same email, signature/trailer appended by one mailbox** | **Not** duplicates — bodies differ | Sanitising signatures is a content-losing heuristic; needs spec guidance |
| **Same email, different body casing** | Not duplicates (case preserved) | Case-folding bodies would merge legitimately different messages on the off-chance they differ only in case |
| **Same email, differing `sentDate` seconds** | Not duplicates | Coarsening to day/minute resolution would increase false merges |
| **Same email copied with different attachments** | Losing copy suppressed **whole**, its attachments disappear with it | Accepted risk (decision 3, Q3): merging attachment sets is a data-model change — an email row plus another email's children |
| **Same email where one copy has attachments and the other has none** | Loser suppressed; winner's set ships | If the loser is the one with attachments, they are lost — this is the sharp edge of Q3. Worth revisiting with the interviewer |
| **Two emails, identical attachments, different content** | Both emails kept, attachments kept under both | This is the deliberate cost of family integrity: the load file contains duplicate bytes, and the ingest job sees them as distinct family members |

### 7.3 Custodians, completeness, and the one place the spec pulls two ways

§4 says each custodian's production set must be complete and self-contained so it can be produced
alone if that custodian is severed. §3.3 says the same email collected from two mailboxes is a
duplicate. In the sample, `mlee`'s copy of the Q3 email (009) is suppressed, so **`mlee`'s
production no longer contains that email at all** — even though the content survives under
`jsmith`.

The implemented reading: "complete" means *family-complete* (no email without its attachments),
and §3.3's explicit dedup instruction wins over custodian-local completeness. This tension is not
recorded in `DECISIONS.md` as a numbered decision — it is the strongest candidate for the next
question to the interviewer. If custodian completeness must win, the change is: key email
fingerprint groups by `(custodian, fingerprint)` (or drop cross-mailbox dedup for any email whose
attachments differ), which increases row counts and reintroduces reviewer-visible duplicates.

Also relevant: suppression is batch-global, so a suppressed document is suppressed for **every**
custodian; there is no per-custodian output mode. Producing per-custodian load files would mean
running the tool per custodian subset — which changes email fingerprint grouping (fewer copies to
compare) and can change *which* copy survives.

### 7.4 Input anomalies (mostly TD-1 territory)

| Input | Current behaviour |
|---|---|
| Duplicate `docId` | Second occurrence: `duplicate docId`, skipped; lookups resolve first-wins. Which one is "first" depends on file order → the any-permutation promise is read as applying to valid batches (TD-4, decision 15) |
| Empty / whitespace `docId` | `empty docId` warning; `(missing docId)` shown on stderr; the document is skipped. Nothing can address it as a parent (a child with `parentId: ""` hits `unknown parentId ""` on its own), so it drags no descendants |
| Empty `sha256` | `empty sha256` warning; doc skipped (it could otherwise join every empty-hash group) |
| Missing/offset-less/badly formatted `sentDate` on an email | warning; doc skipped with descendants. **Operational risk:** if upstream ever emits naive timestamps, whole families vanish with exit 0 — watch the warning count |
| `docType: "Email"` (wrong case) | **Accepted silently**: not validated as an email, not deduplicated, row emitted with `DocType=Email` and empty `SentDate` (verified by probe) |
| Unknown `docType` (e.g. `"folder"`) | Same as above: never grouped, always emitted, `FamilyID` = root |
| Uppercase hex `sha256` | Different string → **no** duplicate match (ordinal compare, verified by probe) |
| Non-padded ids (`DOC-9` vs `DOC-10`) | Ordinal ordering and lowest-id survivor: `DOC-10` beats `DOC-9`. Fine for fixed-width ids, surprising for variable-width |
| Non-ASCII file names | Pass through as UTF-8 (no BOM); quoted only when they contain `",\r\n` |
| Fields containing commas/quotes/newlines | Quoted per RFC 4180; header never escaped (TD-2: ingest tolerance unverified) |
| Extra unknown JSON fields | Ignored by `System.Text.Json` |
| `"documents": []` / missing array | Valid run: `0 in, 0 out` |

A useful habit: `grep '^warning:' stderr.log | sort | uniq -c` after a real batch. Warnings are the
only signal that rows disappeared for *input* reasons rather than dedup.

### 7.5 Scale (§6.2: hundreds → ~2M documents)

Today's implementation is in-memory end to end: `File.ReadAllText` → JSON string → object graph →
`HashSet`/`Dictionary` structures → `StringBuilder` for the whole CSV → one write. Rough cost at
2M documents: several GB of managed memory (JSON text + documents + rows + CSV copy), all live at
once. It is O(n) in every pass (memoised root and cycle resolution included), so *time* scales
fine; *memory* is the ceiling.

If 2M becomes routine, the changes, in order of value:

1. Stream rows into the file (`StreamWriter`) instead of building the CSV string — removes one
   full copy and the sort's buffering pressure;
2. Deserialize with `JsonSerializer.DeserializeAsyncEnumerable` — keeps one document array instead
   of the text plus the graph;
3. Only if still short: spool documents to a temporary store and do the grouping in merge passes.

Any of those keeps the grouping logic identical because the pipeline is already three independent
scans over `IReadOnlyList<Document>`.

### 7.6 Output / ingest contract

| Change | Effect |
|---|---|
| Reordering or renaming columns, changing header text | **Breaks ingest.** The header is a `const` and a golden test asserts it byte-for-byte |
| Quoting every field instead of only when needed | Output stays valid CSV but differs byte-wise from today; may or may not be tolerated by ingest (TD-2) |
| CRLF line endings | Different bytes on every line; ingest expects LF |
| Removing escaping | A comma in a filename splits a row — silent column shift downstream |
| Changing sort to culture-aware `string.Compare` | Output becomes machine-dependent — the §6.1 promise dies |
| Numeric/natural sort of `DocID` | Different byte order; only affects row order, which §5 declares insignificant, but breaks golden tests |
| Adding a BOM | Spreadsheet-friendlier, ingest-uncertain; also changes every byte |

### 7.7 Metrics and reporting

`suppressed` counts duplicates **and** dragged descendants; `skipped` (invalid) is invisible in the
summary. If someone wants "documents removed for any reason", they need to add the warning count —
or the summary line changes (a contract change for anyone parsing stdout).

---

## 8. Change guide — "I want to change X"

| Goal | Touch | Tests that will tell you |
|---|---|---|
| Different survivor rule (e.g. highest `docId`, newest date) | `CollectLooseNonSurvivors`, `CollectAttachmentNonSurvivors`, `CollectEmailNonSurvivors` (each picks `survivor` in one loop) | `DeduperTests`, `DeterminismTests`, `GoldenE2ETests` |
| Different normalization for email matching | `EmailFingerprint` (one method per field) | `EmailFingerprintTests` (table of 8 fixtures + case-guard), `DeduperTests` cross-mailbox group |
| Attachment key by family root | `CollectAttachmentNonSurvivors` — replace `d.ParentId` with `index.ResolveRoot(d).DocId` | `DeduperTests` (INGEST-4471 regression test will fail — read it before changing it) |
| Skip attachments of a losing family differently | `CollectDraggedDescendants` | `DeduperTests` propagation, `ExclusionTests` |
| New validation rule | `Validator.FirstProblem` (+ its position in the order) | `ValidatorTests`, `CliTests` warning-format tests |
| New output column | `LoadFileRow`, `LoadFileWriter.EscapeField` call list, `Header`, golden CSV | `LoadFileWriterTests`, `GoldenE2ETests` |
| Different date format | `IsoDate.FormatUtc` (+ validator regex if input shape changes) | `RowProjectionTests`, `GoldenE2ETests` |
| Warnings as JSON / different exit codes | `Program.Main` | `CliTests` |
| Per-custodian output | new grouping dimension + CLI flag — a product change, not a tweak | none exist; write golden tests for two custodians first |

Rule of thumb: **any change to a grouping key or a survivor rule must be paired with a shuffle
test**, because that is the only class of bug that stays invisible in a green single-order run.

---

## 9. Tests — what is proven, what only looks proven

Run them with:

```bash
dotnet test test/DedupTool.Tests/DedupTool.Tests.csproj    # 123 tests, ~70 ms
```

(`dotnet test` at the repo root fails on this machine — SDK 8.0.133 cannot read `DedupTool.slnx`.
TD-3. That is an SDK/tooling issue, not a test issue.)

Counts below are **executed test cases** (theory rows expand), totalling 123:

| File | Cases | Focus |
|---|---:|---|
| `ValidatorTests` | 20 | every invalid-input reason, warning order, cycle memoisation |
| `FamilyIndexTests` | 8 | simple/nested/loose roots, unknown parent, cycle guard, memo |
| `ExclusionTests` | 8 | invalid docs and descendants skipped, never counted as suppressed |
| `DeduperTests` | 27 | three dedup classes, class disjointness, propagation, INGEST-4471 regression, cross-mailbox grouping, attached-email exclusion |
| `RowProjectionTests` | 13 | `FamilyID`/`SentDate` from root, UTC formatting (incl. negative offsets, midnight rollover), empty dates for loose files |
| `EmailFingerprintTests` | 9 | normalization table (8 fixtures), case-only variants do **not** match |
| `LoadFileWriterTests` | 15 | header bytes, escaping cases, LF, ordinal order, stability |
| `DeterminismTests` | 2 | pipeline-level byte identity under shuffles |
| `GoldenE2ETests` | 4 | full inline CSV through real `Program.Main`, 4 shuffle seeds byte-identical, JSON round-trip through the CLI |
| `CliTests` | 13 | usage/exit 2, unreadable/empty/malformed batch/exit 1, warning text, summary line |
| `TestPathsTests`, `TestShuffleTests` | 1 + 3 | the test harness itself |

**Honest gaps** — behaviours reasoned about but not covered by the *sample*:

- the sample has no nested chain, no attached email, no quoting-triggering filename, no invalid
  document — those paths are covered by unit tests only;
- parentless attachments, unknown `docType`, uppercase hashes and empty-string `parentId` are
  documented above from a probe run, not from the committed suite;
- 2M-document scale has never been run (see §7.5).

Adding a test: put console-touching tests in `[Collection("Console")]` (`ConsoleCollection.cs`
disables parallelisation) — otherwise `Program.Main` captures race each other and the suite flakes.

---

## 10. Known limits and open questions

| ID | Question | Current stance |
|---|---|---|
| TD-1 | Malformed batches: fail-fast or best-effort? | Best-effort + warnings, exit 0 (provisional, implemented) |
| TD-2 | Does the ingest job accept RFC 4180 quoting? | Assumed yes; unverifiable from here |
| TD-3 | Root `dotnet test` broken (`.slnx` vs SDK 8) | Documented workaround: csproj path |
| TD-4 | Duplicate `docId`s make output order-sensitive | First occurrence wins; any-permutation promise read as valid-batch-only |
| — | §4 custodian completeness vs §3.3 cross-mailbox suppression | §3.3 wins; not yet put to the interviewer (see §7.3) |
| — | Q3: losing family's unique attachments are dropped | Accepted risk; suppress-as-is, no merge |

---

## 11. Decision cross-reference

| Decision (`DECISIONS.md`) | Where it lives in code |
|---|---|
| 1. Attachment dedup vs family integrity | `CollectAttachmentNonSurvivors` key `(parentId, sha256)` |
| 2. Lowest `docId` survives, family suppressed whole | survivor loops + `CollectDraggedDescendants` |
| 3. Differing attachment sets → loser suppressed as-is | `CollectEmailNonSurvivors` (no merging exists) |
| 4. Full fingerprint normalization | `EmailFingerprint.Compute` |
| 5. RFC 4180 CSV | `LoadFileWriter.EscapeField` |
| 6. Malformed input → best effort | `Validator` + `Program` warning loop, exit 0 |
| 7. Dedup classes disjoint | three separate type-filtered scans |
| 8. Invalid docs are not "suppressed" | `CollectSkipped` vs `duplicates` split in `Run` |
| 9. Attachment key is the literal parent | `CollectAttachmentNonSurvivors` |
| 10. Nested emails are family members | `CollectEmailNonSurvivors` `parentId is not null` filter |
| 11. Warning format | `Program` `warning: {doc}: {reason}` |
| 12. Exit codes 0/1/2 | `Program.Main` |
| 13. Deterministic row order | `LoadFileWriter.ToCsv` ordinal sort |
| 14. FamilyID/SentDate from root | `index.ResolveRoot` in projection |
| 15. Duplicate `docId`s order-sensitive | `FamilyIndex` first-wins, `Validator` duplicate check |

---

## 12. Glossary

- **Family** — a top-level email plus everything under it (attachments, attached emails, their
  attachments). Identity is the root `docId`.
- **Loose file** — a document with no parent, not an email: a file-share document.
- **Suppressed** — a duplicate (or a descendant of one) omitted from the load file and counted.
- **Skipped** — an invalid document (or a descendant of one) omitted from the load file, warned
  about, and **not** counted.
- **Dragged** — a valid document removed because an ancestor was suppressed; it is a suppressed
  document for counting purposes.
- **Fingerprint** — normalized five-field email identity used for §3.3 dedup.
- **Load file** — the CSV the ingest job consumes; the tool's only product.
