#!/usr/bin/env python3
"""Regenerate the curated transcripts in this folder from ai/ITERATION_LOG.md.

The transcripts are derived, not hand-maintained: prompts are copied verbatim from the
log's "next-task prompt" blockquotes, and the assistant side is condensed from each
entry's bullets. Run this after appending to the log so the two cannot drift:

    python3 ai/transcripts/generate_transcripts.py
"""

import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LOG = os.path.join(REPO, "ai", "ITERATION_LOG.md")
OUT = HERE

BLANK = "\x00BLANK\x00"


def condense(text, limit):
    text = text.replace("`", "")
    text = re.sub(r"\*\*|\*", "", text)
    if len(text) > limit:
        text = text[: limit - 1].rstrip() + "…"
    return text


def parse_entry(body):
    """Return (bullets, prompt) for one log entry."""
    if "**Done:**" in body:
        sec = body.split("**Done:**", 1)[1]
        sec = re.split(r"\n\*\*[A-Z]", sec)[0]
    else:
        # Iteration 0 keeps its content under Read/Verified/Diagnosis/Questions.
        sec = body

    bullets = []
    for line in sec.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith(">"):
            continue
        if re.match(r"^\*\*Next-task prompt", stripped):
            continue
        heading = re.match(r"^\*\*(.+?):?\*\*\s*(.*)$", stripped)
        if heading:
            label = heading.group(1).rstrip(":")
            rest = heading.group(2).strip()
            if bullets:
                bullets.append(BLANK)
            bullets.append(f"_{condense(label, 80)}_")
            if rest:
                bullets.append("- " + condense(rest, 420))
            continue
        m = re.match(r"^(\s*)- (.*)$", line)
        if not m:
            continue
        indent = "    " if len(m.group(1)) >= 2 else ""
        bullets.append(indent + "- " + condense(m.group(2).strip(), 300 if indent else 420))

    while bullets and bullets[0] == BLANK:
        bullets.pop(0)
    while bullets and bullets[-1] == BLANK:
        bullets.pop()
    # Collapse accidental double blanks left by back-to-back headings.
    tidy = []
    for b in bullets:
        if b == BLANK and tidy and tidy[-1] == BLANK:
            continue
        tidy.append(b)

    prompt = ""
    pm = re.search(r"\*\*Next-task prompt[^\n]*\*\*\n\n((?:> .*\n?)+)", body)
    if pm:
        prompt = "\n".join(l[2:].rstrip() for l in pm.group(1).strip().splitlines())
    return tidy, prompt


text = open(LOG).read()
entries = {}
for p in re.split(r"(?m)^## Iteration ", text)[1:]:
    m = re.match(r"(\d+)\s*—\s*([^\n]*)", p)
    if not m:
        continue
    bullets, prompt = parse_entry(p[m.end():])
    entries[int(m.group(1))] = {"bullets": bullets, "prompt": prompt}

PHASES = [
    ("01-spec-review-and-openspec.md", "Spec review and OpenSpec change", range(0, 2),
     "Review of the existing tool and spec, resolution of Q1–Q7, and drafting the OpenSpec change plus the 32-task plan."),
    ("02-scaffolding-and-family-model.md", "Test scaffolding and the family model", range(2, 8),
     "Factory helpers, repo-root resolver and deterministic shuffle, then the docId/children index, the validation pass, and exclusion of invalid documents with their descendants."),
    ("03-class-scoped-dedup-and-fingerprints.md", "Class-scoped dedup and email fingerprints", range(8, 16),
     "Loose, attachment and email dedup; class disjointness; suppression propagation; fingerprint normalization and cross-mailbox grouping with whole-family suppression; attached-email exclusion; determinism."),
    ("04-row-projection-and-load-file.md", "Row projection and the load-file writer", range(16, 22),
     "FamilyID and SentDate from the resolved family root, UTC formatting, and RFC 4180 escaping with a byte-exact header and ordinal DocID ordering."),
    ("05-cli-reporting-and-e2e.md", "CLI reporting and end-to-end verification", range(22, 29),
     "Warnings on stderr, the input-error boundary, the usage contract, the summary line, the golden end-to-end CSV, order independence, and the full-suite check."),
    ("06-documentation-and-closeout.md", "Documentation and close-out", range(29, 35),
     "DECISIONS.md, the tech-debt register, the questions/assumptions register, the iteration-log close-out, and these transcripts."),
]

HEADER = """# Transcript — {name}

Curated transcript of the working session for phase **{name}** (iterations {lo}–{hi}).
{about}

**Provenance.** The prompts below are reproduced **verbatim** — they are the
"next-task prompt" texts recorded in `ai/ITERATION_LOG.md`, which the human pasted
back for each iteration after reviewing the previous task. The assistant side is a
**condensed record** of what was implemented and verified in that iteration, taken
from the same log; it is not a raw dump of model output. The unedited conversation
lives in the client — export it from the client UI if a byte-exact log is required.

Regenerated from `ai/ITERATION_LOG.md` by `generate_transcripts.py` in this folder, so
the two cannot drift.

---
"""

for fname, name, rng, about in PHASES:
    lo, hi = rng.start, rng.stop - 1
    chunks = [HEADER.format(name=name, lo=lo, hi=hi, about=about), "\n"]
    for i in range(rng.start, rng.stop):
        if i == 0:
            chunks.append("## Iteration 0\n\n**Prompt received (verbatim, session brief):**\n\n"
                          "> Review the existing DedupTool code and SPEC.md against the batch-dedup brief, "
                          "identify the defects (starting with INGEST-4471), and raise every question the "
                          "documents cannot answer before any code is written.\n\n")
        else:
            prev = entries.get(i - 1, {}).get("prompt", "")
            if prev:
                quoted = "\n".join("> " + l if l else ">" for l in prev.splitlines())
                chunks.append(f"## Iteration {i}\n\n**Prompt received (verbatim):**\n\n{quoted}\n\n")
            else:
                chunks.append(f"## Iteration {i}\n\n**Prompt received:** (not recorded)\n\n")
        bullets = entries.get(i, {}).get("bullets", [])
        chunks.append("**Assistant work (condensed):**\n\n")
        body = "\n".join(bullets) if bullets else "*not recorded*"
        chunks.append(body + "\n\n")
    out = "".join(chunks).replace(BLANK + "\n", "\n")
    open(os.path.join(OUT, fname), "w").write(out)
    print("wrote", fname, len(out), "bytes")
