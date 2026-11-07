# Technical Debt — open questions we could not answer

Questions that cannot be resolved from `SPEC.md`, `README.md` or `DECISIONS.md`, and that we could not settle between us. Each entry: the question, why it's open, the provisional behavior, and what would close it.

Status at review (2026-11-06): **TD-1 provisionally implemented** · TD-2, TD-3, TD-4 open.

## TD-1 — Behavior on malformed batches · **provisionally implemented**

- **Question:** the spec *guarantees* `parentId` present, valid ISO dates, unique `docId`s, but is silent on violations: unparseable `sentDate`, missing parent, duplicate `docId`, empty `sha256`, `parentId` cycles.
- **Why open:** not answerable from the documents; would need the interviewer.
- **Provisional (user decision 2026-11-06, see ai/QUESTIONS.md Q6):** Option 2 — **best effort + warnings**: skip invalid documents, print warnings to stderr, exit 0. Never emit a wrong-format `SentDate`; never emit an orphan row (descendants are skipped with their parent).
- **Shipped behavior (tasks 2.3, 7.1, 7.2):**
  - an invalid document and **all its descendants** are skipped — no row, one warning each: `warning: <docId>: <reason>` on stderr, in input order, before the load file is written;
  - skipped documents are **not** counted as `suppressed` and never occupy a dedup key (dedup spec: skipped ≠ duplicate);
  - exit code stays **0** and the load file is written for the valid remainder;
  - an **unreadable or malformed batch file** is a different class: `error: cannot read batch "<path>": <message>` on stderr, exit **1**, and no load file is created (the writer is never reached, so an existing file is not truncated either).
- **Evidence:** `ExclusionTests` (8 tests, 2.3), `CliTests` (unknown-parent, empty-docId, malformed/missing/empty input, 7.1–7.2), one crafted batch through the real CLI → `4 in / 1 out / 0 suppressed` with three warnings.
- **Closes when:** the interviewer confirms best-effort (exit 0) vs fail-fast (exit ≠ 0).

## TD-2 — RFC 4180 quoting vs the real ingest job · **open**

- **Question:** we chose RFC 4180 quoting (ai/QUESTIONS.md Q5), but cannot verify that the ingest job parses quoted fields — the spec only pins column order and header text.
- **Why open:** no access to the ingest job or a real load file containing quoted values.
- **Provisional:** RFC 4180 quoting on all eight fields, header emitted verbatim from the constant, LF line endings only, UTF-8 without BOM (tasks 6.1–6.2).
- **Status:** implemented and covered — quoting/truncation rules by unit tests, and an end-to-end batch with `Quarterly "final", v2.eml` re-reads exactly through Python's `csv` reader. The shipped sample is unchanged because none of its fields need quoting, so that fixture proves nothing about escaping.
- **Risk if the choice is wrong:** the ingest job would mis-split rows with commas in file names; ordinary rows are unaffected either way.
- **Closes when:** the ingest team confirms, or we see a real load file containing quoted/odd file names.

## TD-3 — README test instructions do not work on this machine · **open**

- **Question:** the brief says to run `dotnet test` at the repo root; that command fails here.
- **Reproduced 2026-11-06:** `dotnet test` at the root → exit **1**, output:
  `MSBUILD : error MSB1003: Specify a project or solution file. The current working directory does not contain a project or solution file.`
- **Cause:** the repo root contains only `DedupTool.slnx` (the newer solution format) and the installed SDK is **8.0.133**; SDK 8 cannot read `.slnx` (needs ≥ 9.0.200), so it reports "no project or solution" rather than a version complaint.
- **Workaround that works today:** `dotnet test test/DedupTool.Tests/DedupTool.Tests.csproj` → **123/123 passing**.
- **Options (need user decision — no code changes without approval):** add a classic `.sln`, document the csproj invocation in the README, or require SDK 9.
- **Why not fixed here:** all three change the repo's build entry point or developer requirements, which is outside this change's scope; the full suite is green via the workaround.
- **Closes when:** user picks an option.

## TD-4 — Batches with duplicate `docId`s are order-sensitive · **open (documented, accepted)**

- **Question:** dedup spec §6.1 requires the surviving set, FamilyID/SentDate values and load-file bytes to be identical for **any** permutation of the input. The same spec (input-handling) also says the *first* occurrence of a duplicated `docId` is processed and later ones are skipped — so which duplicate is "first" depends on the permutation.
- **Why open:** both statements are in the spec; resolving it needs the interviewer's intent (reject such batches? tie-break by content? treat ids as guaranteed unique?).
- **Provisional:** first occurrence wins (tasks 2.1–2.3); later occurrences warn `duplicate docId` and are skipped together with their descendants. The any-permutation promise is read as applying to **valid** batches. Determinism coverage (4.4 at Deduper level, 8.2 end-to-end) uses the valid sample.
- **Decision record:** `DECISIONS.md` decision 15.
- **Closes when:** the interviewer confirms the reading, guarantees unique `docId`s upstream (then it is a non-issue), or specifies a deterministic tie-break.
