# Spec Delta

## Purpose

Define how the tool behaves when a batch violates the input guarantees the spec assumes, how it reports errors and warnings, and what it prints on success.

## ADDED Requirements

### Requirement: Command-line contract
The tool SHALL be invoked as `<tool> <batch.json> <loadfile.csv>`. With exactly two arguments it processes the batch and exits 0 on success; with any other argument count it MUST write `usage: DedupTool <batch.json> <loadfile.csv>` to stderr and exit 2.

#### Scenario: Wrong argument count
- **WHEN** the tool is invoked with one argument
- **THEN** the usage line is written to stderr and the exit code is 2

#### Scenario: Successful run exits zero
- **WHEN** the tool is invoked with two valid arguments and processing completes
- **THEN** the exit code is 0

### Requirement: Unusable batch file fails the run
When the batch file cannot be read or is not valid JSON, the tool MUST write an error to stderr and exit with a non-zero code, without writing a load file.

#### Scenario: Malformed JSON
- **WHEN** the input file contains invalid JSON
- **THEN** an error is written to stderr, no load file is created, and the exit code is non-zero

### Requirement: Invalid documents are identified and skipped with a warning
A document is invalid when its parentId references a docId not present in the batch, its sha256 is empty or missing, its docId is empty or already used by an earlier document, its parentId chain contains a cycle, or (for an email) its sentDate cannot be parsed as ISO 8601 with offset. Each invalid document MUST be skipped and MUST produce one warning line on stderr naming the docId and reason.

#### Scenario: Unknown parent reference
- **WHEN** an attachment lists parentId "DOC-MISSING" which is not in the batch
- **THEN** a warning naming the doc is written to stderr, the document is omitted, and the exit code is 0

#### Scenario: Unparseable sentDate is never emitted in a wrong format
- **WHEN** an email has sentDate "last Tuesday"
- **THEN** a warning naming the doc is written to stderr, the email is omitted, and no row with an unformatted date reaches the load file

#### Scenario: Duplicate docId
- **WHEN** two documents share docId "DOC-1" and the second appears later in the input
- **THEN** the first occurrence is processed normally, the second is skipped with a warning, and the exit code is 0

#### Scenario: ParentId cycle
- **WHEN** two documents list each other as parentId
- **THEN** both are skipped with warnings, the run exits 0, and no emitted row references either as ParentID

#### Scenario: Empty sha256
- **WHEN** a document has no sha256 value
- **THEN** it is skipped with a warning and the run still exits 0

#### Scenario: Skipping a document skips its descendants
- **WHEN** an invalid document has children in the batch
- **THEN** those children are also skipped with warnings, so no emitted row references the skipped docId as ParentID

### Requirement: Invalid documents do not fail the run
When invalid documents are skipped the run MUST still exit 0 and write a load file for the valid remainder. Skipped documents MUST NOT be counted as suppressed, and their descendants MUST be skipped with them so no emitted row references a skipped docId as ParentID.

#### Scenario: Batch with invalid documents still succeeds
- **WHEN** some documents in the batch are invalid and others are valid
- **THEN** the exit code is 0, a load file is written containing only valid documents, and the summary counts exclude the skipped documents from "suppressed"

#### Scenario: Valid batch produces no warnings
- **WHEN** every document in the batch satisfies the input guarantees
- **THEN** nothing is written to stderr

### Requirement: One-line success summary
On success the tool SHALL print exactly one line to stdout: `<batchId>: <in> documents in, <out> out, <suppressed> suppressed as duplicates.` where <in> is the number of documents in the input file, <out> the number of rows written, and <suppressed> the number of documents suppressed as duplicates.

#### Scenario: Sample batch summary
- **WHEN** the tool processes data/batch-sample.json successfully
- **THEN** stdout contains "batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates."

#### Scenario: Summary counts match the load file
- **WHEN** a batch is processed successfully
- **THEN** <out> equals the number of data rows in the load file and <in> equals <out> plus <suppressed> when no document was skipped as invalid
