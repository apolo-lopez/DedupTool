# Spec Delta

## Purpose

Define the exact CSV load file the review platform ingest job consumes: fixed header and column order, family and date derivation from the batch's document tree, CSV quoting rules, and deterministic row ordering.

## ADDED Requirements

### Requirement: Fixed header and column order
The load file SHALL start with exactly the header line `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`, and every data row SHALL place values in that column order. Line endings are LF on every row.

#### Scenario: Header is byte-exact
- **WHEN** any batch is processed successfully
- **THEN** the first line of the load file is exactly "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash" followed by LF

#### Scenario: Row column order
- **WHEN** a row is emitted for a document
- **THEN** its fields appear in the order DocID, FamilyID, ParentID, Custodian, DocType, FileName, SentDate, Hash

### Requirement: FamilyID is the family's top-level document
FamilyID SHALL be the docId at the root of the document's parentId chain — for an email family, the top-level email; for a loose file, its own docId.

#### Scenario: Nested attachment chain resolves to the root email
- **WHEN** the chain is email DOC-1 → attachment DOC-2 → attachment DOC-3
- **THEN** the rows for DOC-2 and DOC-3 both carry FamilyID DOC-1

#### Scenario: Simple attachment family
- **WHEN** attachment DOC-000016 has parent email DOC-000015
- **THEN** its row carries FamilyID DOC-000015

#### Scenario: Loose file is its own family
- **WHEN** a loose document with docId DOC-000007 is emitted
- **THEN** its row carries FamilyID DOC-000007 and an empty ParentID

### Requirement: ParentID mirrors the input parentId
ParentID SHALL be the document's parentId, or the empty string when parentId is null.

#### Scenario: Top-level document has empty ParentID
- **WHEN** an email or loose document with null parentId is emitted
- **THEN** its ParentID field is empty

### Requirement: SentDate is UTC, formatted exactly YYYY-MM-DDTHH:MM:SSZ
SentDate SHALL be converted to UTC and formatted as `YYYY-MM-DDTHH:MM:SSZ` — four-digit year, no fractional seconds, no offset. Conversion truncates fractional seconds. A document with a parent carries its family root email's date; a loose file carries an empty SentDate.

#### Scenario: Offset input converts to UTC
- **WHEN** an email has sentDate "2024-03-05T14:22:00-05:00"
- **THEN** its SentDate field is exactly "2024-03-05T19:22:00Z"

#### Scenario: Z input stays unchanged in value
- **WHEN** an email has sentDate "2024-03-07T16:00:00Z"
- **THEN** its SentDate field is exactly "2024-03-07T16:00:00Z"

#### Scenario: Fractional seconds are truncated
- **WHEN** an email has sentDate "2024-03-05T19:22:00.789Z"
- **THEN** its SentDate field is exactly "2024-03-05T19:22:00Z"

#### Scenario: Attachment carries the family root date
- **WHEN** attachment DOC-000003 belongs to email DOC-000001 with sentDate "2024-03-05T14:22:00-05:00"
- **THEN** the row for DOC-000003 carries SentDate "2024-03-05T19:22:00Z"

#### Scenario: Nested child carries the root date, not an empty one
- **WHEN** attachment DOC-3 (parent DOC-2, grandparent email DOC-1 with sentDate "2024-03-05T19:22:00Z") is emitted
- **THEN** the row for DOC-3 carries SentDate "2024-03-05T19:22:00Z"

#### Scenario: Loose file has empty SentDate
- **WHEN** a loose document is emitted
- **THEN** its SentDate field is empty

### Requirement: RFC 4180 quoting with LF line endings
Fields containing a double quote, comma, or line feed SHALL be enclosed in double quotes with embedded double quotes doubled; all other fields SHALL be emitted unquoted. The file still ends every row with LF.

#### Scenario: File name containing a comma
- **WHEN** a document has fileName "budget, final.xlsx"
- **THEN** its FileName field is emitted as "budget, final.xlsx" including the surrounding quotes

#### Scenario: File name containing a double quote
- **WHEN** a document has fileName 'say "hi".txt'
- **THEN** its FileName field is emitted as "say ""hi"".txt" including the surrounding quotes

#### Scenario: Ordinary fields stay unquoted
- **WHEN** a field contains none of comma, double quote, or line feed
- **THEN** it is emitted without quotes, so ordinary rows are byte-identical to today's format

### Requirement: Deterministic row order
Rows SHALL be ordered by DocID using ordinal string comparison, so the same batch always produces the same bytes regardless of input document order.

#### Scenario: Byte-identical output for any input order
- **WHEN** the tool processes two permutations of the same batch
- **THEN** the two load files are byte-identical
