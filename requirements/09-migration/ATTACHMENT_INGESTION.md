# Bulk Attachment Ingestion Specification

## Supported inputs
- ZIP archive
- Manifest plus accessible folder path where deployment permits
- SharePoint/library adapter
- Direct multi-file upload for smaller batches

## Preferred manifest columns
- SourceRecordId
- AgreementReference
- RelativePath
- FileName
- DocumentType
- DocumentDate
- VersionLabel
- IsExecutedCopy
- ParentDocumentSourceId
- Sha256 (optional input, always computed by platform)
- Notes

## Matching order
1. Explicit SourceRecordId in manifest
2. Exact AgreementReference in manifest
3. Configured regular expression/naming rule
4. Exact parent folder name matching agreement reference
5. Manual resolution queue

The platform must show why each match was proposed and its method. Do not use probabilistic matching without explicit human confirmation.

## Validation
- Archive path traversal prevention
- Maximum archive, file and item counts
- Extension allowlist and MIME/signature inspection
- Empty/corrupt/encrypted file detection
- Duplicate hashes
- Duplicate file names within a contract
- Orphan manifest entries and orphan files
- Target contract and tenant validation
- Malware scanning provider hook

## Version and duplicate options
- Reject duplicate hash
- Link existing content where storage permits
- Add new document version
- Keep as separate document with warning
- Manual review

## Results
Display total files, matched, unmatched, invalid, duplicate, imported and failed. Export detailed results. Unmatched or failed files remain available in a restricted staging area until resolved or deliberately discarded through an audited action.
