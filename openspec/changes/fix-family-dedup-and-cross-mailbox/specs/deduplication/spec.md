# Spec Delta

## Purpose

Decide which documents of a collection batch are suppressed as duplicates and which reach the load file, across all custodians, while guaranteeing that surviving email families stay intact and that the outcome never depends on input order.

## ADDED Requirements

### Requirement: Loose files are deduplicated by content hash
Two loose documents with the same sha256 SHALL be treated as duplicates. Deduplication SHALL span the entire batch, across all custodians.

#### Scenario: Same loose content in two custodians
- **WHEN** the batch contains loose documents DOC-000008 (jsmith) and DOC-000013 (mlee), both with sha256 "5395fc0e…"
- **THEN** exactly one of them appears in the load file and the other is counted as suppressed

#### Scenario: Distinct loose content is kept
- **WHEN** two loose documents carry different sha256 values
- **THEN** both appear in the load file and neither is counted as suppressed

### Requirement: Attachments are deduplicated by content hash within the same parent
Two documents with docType "attachment" that share the same parentId and the same sha256 SHALL be treated as duplicates, and only one copy SHALL be kept under that parent. Attachments with the same sha256 under different surviving parents are not duplicates: family integrity outranks batch-wide attachment hashing.

#### Scenario: Identical attachments on the same email
- **WHEN** one email has two attachment documents with the same sha256 and the same parentId
- **THEN** only one of them appears in the load file under that parent

#### Scenario: Same file attached to two different surviving emails
- **WHEN** DOC-000003 (parent DOC-000001) and DOC-000005 (parent DOC-000004) carry the same sha256 and both families survive
- **THEN** both DOC-000003 and DOC-000005 appear in the load file

### Requirement: Surviving families never lose attachment content
For every email that appears in the load file, each distinct attachment sha256 present anywhere in that email's family in the input MUST appear on at least one attachment row of that surviving family in the output.

#### Scenario: Forward email keeps its attachment (INGEST-4471)
- **WHEN** DOC-000004 survives and its attachment DOC-000005 shares its sha256 with DOC-000003 in another family
- **THEN** the load file contains a row for DOC-000005 with ParentID DOC-000004

#### Scenario: A suppressed family takes all of its members
- **WHEN** the email DOC-000009 is suppressed as a duplicate of DOC-000001
- **THEN** DOC-000009, DOC-000010 and DOC-000011 are all absent from the load file

### Requirement: Email copies are recognized across mailboxes by content fingerprint
Two email documents are duplicates when their fingerprints match. The fingerprint normalizes formatting only: from/to addresses trimmed and case-folded; recipients compared as a case-folded set (order-insensitive); subject with internal whitespace collapsed (case preserved); body line-endings normalized to LF and outer whitespace trimmed (case preserved); sentDate compared as the UTC instant. sha256, sizeBytes, fileName and content case MUST NOT participate.

#### Scenario: Sample cross-mailbox pair is one email
- **WHEN** DOC-000001 (jsmith, LF body, "2024-03-05T14:22:00-05:00") and DOC-000009 (mlee, CRLF body, "2024-03-05T19:22:00Z") differ only in header case, line endings and date offset
- **THEN** their fingerprints match and they are duplicates

#### Scenario: Case-only content differences are different emails
- **WHEN** two emails are identical except that the body or the subject differs only in letter case
- **THEN** their fingerprints do not match and both survive

#### Scenario: Formatting-only differences are the same email
- **WHEN** two emails differ only in recipient order, subject whitespace, body trailing newline, address case, or date offset representation
- **THEN** their fingerprints match

#### Scenario: Different recipients are different emails
- **WHEN** two emails share sender, subject, body and sentDate but have different recipient sets
- **THEN** their fingerprints do not match and both survive

### Requirement: A duplicate email group collapses to a single family
For each group of email copies with equal fingerprints, exactly one copy SHALL survive: the one with the lowest docId, compared as an ordinal string. The losing copies' entire families — the email and every document descending from it through parentId — SHALL be omitted from the load file.

#### Scenario: Lowest docId survives
- **WHEN** DOC-000001 and DOC-000009 are fingerprint-matched duplicates
- **THEN** DOC-000001 survives and family DOC-000009 is suppressed with all of its descendants

#### Scenario: Non-duplicate emails all survive with their families
- **WHEN** two emails have different fingerprints
- **THEN** both survive and each keeps its own attachments

### Requirement: Suppression never depends on input order
The set of surviving documents, their computed FamilyID and SentDate values, and the resulting load file bytes MUST be identical for any permutation of the input documents.

#### Scenario: Shuffled batch produces the same file
- **WHEN** the sample batch's documents are shuffled and the tool runs on both orders
- **THEN** both runs produce byte-identical load files

### Requirement: Suppressed documents are counted once
Each document omitted because it is a duplicate SHALL count once toward the suppressed total, including every descendant of a suppressed family. Documents skipped as invalid are not duplicates and SHALL NOT count as suppressed.

#### Scenario: Sample suppression total
- **WHEN** the tool processes data/batch-sample.json
- **THEN** the suppressed documents are exactly DOC-000009, DOC-000010, DOC-000011 and DOC-000013 — a total of 4

### Requirement: No orphan rows
When a document is suppressed, all of its descendants MUST be suppressed with it; no emitted row may carry a ParentID whose DocID is not emitted.

#### Scenario: Suppressed middle of a chain drags its children
- **WHEN** in the chain email → DOC-A → DOC-B, DOC-A is suppressed as a duplicate under the same parent
- **THEN** neither DOC-A nor DOC-B appears in the load file and no emitted row references DOC-A or DOC-B as ParentID

### Requirement: Deduplication classes are scoped by document type
A document SHALL only ever be compared against documents of its own class: loose against loose, attachment against attachment, top-level email against top-level email. Equal sha256 values across different classes are not duplicates.

#### Scenario: Attachment hash equal to a loose file hash
- **WHEN** a loose document and an attachment carry the same sha256
- **THEN** both appear in the load file

### Requirement: Top-level emails are fingerprinted; nested emails follow family rules
Only emails without a parentId SHALL participate in cross-mailbox fingerprint grouping. An email document that has a parentId is a family member: it SHALL survive only when its family survives and SHALL be suppressed only with its family.

#### Scenario: Attached email copy is not grouped independently
- **WHEN** a docType "email" document has a parentId and another standalone email has an identical fingerprint
- **THEN** the attached copy is not suppressed by the standalone copy; it remains governed by its own family
