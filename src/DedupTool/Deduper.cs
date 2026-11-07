namespace DedupTool;

/// <summary>
/// Suppresses duplicate documents in a batch. Deduplication is class-scoped
/// (design D3): loose documents deduplicate batch-wide by sha256 with the
/// lowest docId surviving; attachments deduplicate by (parentId, sha256)
/// within the same parent (task 3.2); top-level emails deduplicate
/// cross-mailbox by fingerprint (task 4.2, §3.3 — design D4) with the lowest
/// docId's family surviving; attached emails are family members only, never
/// grouped. Suppression propagates down the children index — a suppressed
/// document drags all of its descendants (task 3.4) so no emitted row ever
/// references a docId that is not emitted itself.
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        var documents = batch.Documents;

        // Validation (design D1 phase 1 / D5): ordered warnings + input positions.
        var issues = Validator.Analyze(documents);

        // Family model (task 2.1): docId index + parentId → children map.
        var index = new FamilyIndex(documents);

        // Exclusion (task 2.3): invalid documents and their descendants leave the
        // survivor set entirely — no row, no suppressed count, one warning each.
        var skipped = CollectSkipped(documents, issues, index);
        var warnings = BuildWarnings(documents, issues, skipped);

        // Loose duplicate groups (task 3.1, design D3): batch-wide by sha256,
        // the lowest docId of each group survives — independent of input order.
        var looseNonSurvivors = CollectLooseNonSurvivors(documents, skipped);

        // Attachment duplicate groups (task 3.2, design D3): (parentId, sha256)
        // — dedup is scoped to the same parent, so children of DIFFERENT
        // parents never suppress each other (family integrity outranks
        // batch-wide attachment hashing, ai/QUESTIONS.md Q1).
        var attachmentNonSurvivors = CollectAttachmentNonSurvivors(documents, skipped);

        // Top-level email fingerprint groups (task 4.2, design D3/D4): emails
        // WITHOUT a parentId only — attached emails are family members that
        // never group (task 4.3) and are suppressed only with their family.
        // This replaces the launch-state first-seen sha256 email dedup:
        // identical raw bytes imply identical fingerprints, and a sha-keyed
        // suppression of an attached email would violate the spec's "shall be
        // suppressed only with its family".
        var emailNonSurvivors = CollectEmailNonSurvivors(documents, skipped);

        // Suppression propagation (task 3.4, design D1 phase 4): every
        // non-surviving document drags its descendants out of the output —
        // with the parent gone the child would be an orphan row. Dragged
        // descendants count as suppressed (duplicates' fallout, per the dedup
        // spec: "including every descendant of a suppressed family"), unlike
        // the skipped (invalid) set. Email fingerprints are the widest seed
        // class (§3.3 losing families collapse wholesale).
        var duplicates = new HashSet<Document>(looseNonSurvivors);
        duplicates.UnionWith(attachmentNonSurvivors);
        duplicates.UnionWith(emailNonSurvivors);
        duplicates.UnionWith(CollectDraggedDescendants(documents, skipped, duplicates, index));

        var rows = new List<LoadFileRow>();
        var suppressed = 0;

        foreach (var d in documents)
        {
            if (skipped.Contains(d))
            {
                // Omitted from output AND from dedup/suppressed accounting:
                // a skipped document must never occupy a dedup key either.
                continue;
            }

            if (duplicates.Contains(d))
            {
                // Non-survivor of its class group — loose dup, same-parent
                // attachment dup, losing fingerprint email, or dragged down by
                // a suppressed ancestor (task 3.4) — omitted, counted once.
                // Class checks upstream guarantee survivor sets never overlap
                // across classes.
                suppressed++;
                continue;
            }

            // FamilyID and SentDate both come from the RESOLVED family root
            // (tasks 5.1 + 5.3, design D2/D7) — never from the immediate parent:
            // in a nested chain (email → attachment → attachment) every row
            // reports the root email's docId AND its date. Resolving once per row
            // is cheap (memoized) and makes the three cases fall out uniformly:
            // a top-level email resolves to itself, a loose file resolves to
            // itself with no timestamp → empty cell.
            var familyRoot = index.ResolveRoot(d);
            var familyId = familyRoot.DocId;

            // FormatUtc returns null only for unparseable input, which validation
            // has already excluded; fall back to an empty cell rather than emit a
            // raw non-UTC timestamp.
            var sentDate = familyRoot.SentDate is null
                ? ""
                : IsoDate.FormatUtc(familyRoot.SentDate) ?? "";

            rows.Add(new LoadFileRow
            {
                DocId = d.DocId,
                FamilyId = familyId,
                ParentId = d.ParentId ?? "",
                Custodian = d.Custodian,
                DocType = d.DocType,
                FileName = d.FileName,
                SentDate = sentDate,
                Hash = d.Sha256,
            });
        }

        return new DedupResult(rows, suppressed, warnings);
    }

    /// <summary>
    /// The documents to exclude: every invalid document plus all of its
    /// descendants, found by BFS down the children map (spec input-handling:
    /// "skipping a document skips its descendants"). Propagation is seeded only
    /// from skipped documents that are the *first* occurrence of their docId —
    /// children address their parent by docId, which resolves first-wins
    /// (design D2), so a duplicate second occurrence must not drag along the
    /// first occurrence's children.
    /// </summary>
    private static HashSet<Document> CollectSkipped(
        IReadOnlyList<Document> documents,
        IReadOnlyList<ValidationIssue> issues,
        FamilyIndex index)
    {
        // HashSet<Document> uses reference equality (Document overrides nothing):
        // duplicate docIds stay distinct entries.
        var skipped = new HashSet<Document>();
        foreach (var issue in issues)
        {
            skipped.Add(documents[issue.InputIndex]);
        }

        var queue = new Queue<string>();
        var enqueued = new HashSet<string>(StringComparer.Ordinal);

        foreach (var issue in issues)
        {
            var d = documents[issue.InputIndex];
            if (!string.IsNullOrEmpty(d.DocId) && ReferenceEquals(index.Find(d.DocId), d))
            {
                if (enqueued.Add(d.DocId))
                {
                    queue.Enqueue(d.DocId);
                }
            }
        }

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var child in index.ChildrenOf(parentId))
            {
                if (!skipped.Add(child))
                {
                    continue; // already skipped (invalid itself, or reached earlier)
                }

                if (!string.IsNullOrEmpty(child.DocId) && ReferenceEquals(index.Find(child.DocId), child))
                {
                    if (enqueued.Add(child.DocId))
                    {
                        queue.Enqueue(child.DocId);
                    }
                }
            }
        }

        return skipped;
    }

    /// <summary>
    /// Loose duplicate groups (task 3.1, design D3): batch-wide by sha256, the
    /// lowest docId (ordinal) of each group survives — so the outcome never
    /// depends on input order (§6.1). Returns every non-surviving loose
    /// document; skipped documents never enter a group.
    /// </summary>
    private static HashSet<Document> CollectLooseNonSurvivors(
        IReadOnlyList<Document> documents,
        HashSet<Document> skipped)
    {
        var groups = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        foreach (var d in documents)
        {
            if (skipped.Contains(d) || !string.Equals(d.DocType, "loose", StringComparison.Ordinal))
            {
                continue;
            }

            if (!groups.TryGetValue(d.Sha256, out var group))
            {
                group = new List<Document>();
                groups[d.Sha256] = group;
            }

            group.Add(d);
        }

        var nonSurvivors = new HashSet<Document>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            var survivor = group[0];
            foreach (var d in group)
            {
                if (string.CompareOrdinal(d.DocId, survivor.DocId) < 0)
                {
                    survivor = d;
                }
            }

            foreach (var d in group)
            {
                if (!ReferenceEquals(d, survivor))
                {
                    nonSurvivors.Add(d);
                }
            }
        }

        return nonSurvivors;
    }

    /// <summary>
    /// Attachment duplicate groups (task 3.2, design D3): key is the literal
    /// (parentId, sha256) pair — spec deduplication: "share the same parentId
    /// and the same sha256" — so the outcome never depends on input order and
    /// same-sha attachments under different parents never suppress each other
    /// (family integrity outranks batch-wide attachment hashing, Q1: INGEST-4471).
    /// Survivor per group = lowest docId (ordinal). Skipped documents never
    /// enter a group; every member's parent is guaranteed surviving by the 2.3
    /// exclusion pass (a skipped parent would have skipped this attachment too).
    /// </summary>
    private static HashSet<Document> CollectAttachmentNonSurvivors(
        IReadOnlyList<Document> documents,
        HashSet<Document> skipped)
    {
        var groups = new Dictionary<(string ParentId, string Sha), List<Document>>();
        foreach (var d in documents)
        {
            if (skipped.Contains(d) || !string.Equals(d.DocType, "attachment", StringComparison.Ordinal))
            {
                continue;
            }

            var key = (d.ParentId!, d.Sha256);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<Document>();
                groups[key] = group;
            }

            group.Add(d);
        }

        var nonSurvivors = new HashSet<Document>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            var survivor = group[0];
            foreach (var d in group)
            {
                if (string.CompareOrdinal(d.DocId, survivor.DocId) < 0)
                {
                    survivor = d;
                }
            }

            foreach (var d in group)
            {
                if (!ReferenceEquals(d, survivor))
                {
                    nonSurvivors.Add(d);
                }
            }
        }

        return nonSurvivors;
    }

    /// <summary>
    /// Cross-mailbox email groups (task 4.2, design D3/D4 — §3.3): top-level
    /// emails (no parentId) group by fingerprint; the group's lowest docId
    /// (ordinal) survives and its family supersedes every losing family via
    /// the task 3.4 drag. Attached emails never enter a group — they are
    /// family members, suppressed only with their family (task 4.3 pins this).
    /// A null fingerprint (missing/unparseable sentDate) is never grouped:
    /// the Validator already excludes such documents upstream, and grouping
    /// an invalid email could only suppress the wrong side of a pair.
    /// </summary>
    private static HashSet<Document> CollectEmailNonSurvivors(
        IReadOnlyList<Document> documents,
        HashSet<Document> skipped)
    {
        var groups = new Dictionary<EmailFingerprint, List<Document>>();
        foreach (var d in documents)
        {
            if (skipped.Contains(d) ||
                !string.Equals(d.DocType, "email", StringComparison.Ordinal) ||
                d.ParentId is not null)
            {
                continue;
            }

            var fingerprint = EmailFingerprint.Compute(d);
            if (fingerprint is null)
            {
                continue; // defensive — Validator guarantees this cannot happen
            }

            if (!groups.TryGetValue(fingerprint, out var group))
            {
                group = new List<Document>();
                groups[fingerprint] = group;
            }

            group.Add(d);
        }

        var nonSurvivors = new HashSet<Document>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            var survivor = group[0];
            foreach (var d in group)
            {
                if (string.CompareOrdinal(d.DocId, survivor.DocId) < 0)
                {
                    survivor = d;
                }
            }

            foreach (var d in group)
            {
                if (!ReferenceEquals(d, survivor))
                {
                    nonSurvivors.Add(d);
                }
            }
        }

        return nonSurvivors;
    }

    /// <summary>
    /// Suppression propagation (task 3.4, design D1 phase 4): BFS down the
    /// children index from every non-surviving document. Children resolve
    /// their parent by docId first-wins (task 2.1), so a child is dragged
    /// only when its RESOLVED parent is exactly the suppressed document — a
    /// last-wins or unrelated same-docId doc must not drag other doc's
    /// children. Grandchildren propagate transitively. Documents already in
    /// `skipped` stay in the skipped pipeline (their descendants were already
    /// excluded by the task 2.3 pass) and never enter `dragged`, so a skipped
    /// document is never double-counted as suppressed.
    /// </summary>
    private static HashSet<Document> CollectDraggedDescendants(
        IReadOnlyList<Document> documents,
        HashSet<Document> skipped,
        HashSet<Document> duplicates,
        FamilyIndex index)
    {
        var dragged = new HashSet<Document>();
        var queue = new Queue<Document>();

        foreach (var seed in duplicates)
        {
            if (!skipped.Contains(seed))
            {
                queue.Enqueue(seed);
            }
        }

        while (queue.Count > 0)
        {
            var doomed = queue.Dequeue();
            foreach (var child in index.ChildrenOf(doomed.DocId))
            {
                if (skipped.Contains(child))
                {
                    continue;
                }

                // ChildrenOf keys off ParentId strings; with duplicate docIds
                // the child's actual parent is the first occurrence, so only
                // that one (if suppressed) drags the child.
                if (!string.IsNullOrEmpty(child.ParentId) &&
                    !ReferenceEquals(index.Find(child.ParentId), doomed))
                {
                    continue;
                }

                if (dragged.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return dragged;
    }

    /// <summary>
    /// One warning per skipped document, in input order (design D5): validation
    /// warnings for invalid documents, a parent-skipped warning for descendants
    /// that were valid on their own.
    /// </summary>
    private static List<ValidationWarning> BuildWarnings(
        IReadOnlyList<Document> documents,
        IReadOnlyList<ValidationIssue> issues,
        HashSet<Document> skipped)
    {
        var byIndex = new Dictionary<int, ValidationWarning>();
        foreach (var issue in issues)
        {
            byIndex[issue.InputIndex] = issue.Warning;
        }

        var warnings = new List<ValidationWarning>();
        for (var i = 0; i < documents.Count; i++)
        {
            if (byIndex.TryGetValue(i, out var warning))
            {
                warnings.Add(warning);
            }
            else if (skipped.Contains(documents[i]))
            {
                // Descendant of a skipped document: valid on its own, but its
                // parent is gone (guaranteed non-null — only docs with a parent
                // can be reached as descendants).
                var d = documents[i];
                warnings.Add(new ValidationWarning(d.DocId, $"parent \"{d.ParentId}\" skipped"));
            }
        }

        return warnings;
    }
}
