# AI conversation transcripts

Transcript files for the session that produced the `fix-family-dedup-and-cross-mailbox`
change (32 micro-tasks, one per iteration, each reviewed before the next).

## Files

| File | Iterations | Covers |
|------|-----------|--------|
| `01-spec-review-and-openspec.md` | 0–1 | Code + spec review, Q1–Q7 resolution, OpenSpec change and the 32-task plan |
| `02-scaffolding-and-family-model.md` | 2–7 | Test scaffolding, family index, validation pass, exclusion of invalid docs and descendants |
| `03-class-scoped-dedup-and-fingerprints.md` | 8–15 | Loose/attachment/email dedup, class disjointness, propagation, fingerprint normalization, cross-mailbox grouping, attached-email exclusion, determinism |
| `04-row-projection-and-load-file.md` | 16–21 | FamilyID/SentDate from the family root, UTC formatting, RFC 4180 escaping, header, ordinal row order |
| `05-cli-reporting-and-e2e.md` | 22–28 | Stderr warnings, input-error boundary, usage contract, summary line, golden e2e, order independence, full suite |
| `06-documentation-and-closeout.md` | 29–34 | `DECISIONS.md`, `TECH_DEBT.md`, `ai/QUESTIONS.md`, log close-out, these transcripts |

## Provenance — read this before quoting anything

- **Prompts are verbatim.** Each iteration's prompt is the "next-task prompt" text
  recorded at the end of the previous entry in `ai/ITERATION_LOG.md`; the human pasted
  that text back after reviewing (and approving or rejecting) the previous task, so the
  transcript's prompt and the log's prompt are the same string.
- **Assistant turns are condensed, not raw.** What appears under "Assistant work" is
  the record of what was implemented, tested and observed in that iteration, as written
  in the log's `Done` bullets. It is deliberately shorter than the model's actual
  output and is not a verbatim reproduction of it.
- **The unedited conversation lives in the client.** These files were produced by the
  agent inside the session and cannot contain the byte-exact message stream; export it
  from the client UI if that is required.
- Nothing in these files asserts a result that is not also covered by the log and the
  test suite (`dotnet test test/DedupTool.Tests/DedupTool.Tests.csproj`, 123 tests).

## Regenerating

The files are derived from the log rather than hand-edited:

```bash
python3 ai/transcripts/generate_transcripts.py
```

The script copies prompts verbatim from the `**Next-task prompt (iteration N):**`
blockquotes and condenses each entry's `Done` bullets, so the log stays the single
source of truth. If the log is updated, re-run it rather than editing transcripts by
hand.
