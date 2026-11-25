-- A document kind for the cover pages used to sign a contract off internally (D-95), for new contracts and imported files alike.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Document DROP CONSTRAINT CK_Document_Kind;
GO
ALTER TABLE contracts.Document ADD CONSTRAINT CK_Document_Kind
    CHECK (Kind IN (N'Draft', N'FinalAgreement', N'SignedCopy', N'Schedule', N'LegalMarkup', N'Variation', N'Correspondence', N'Supporting', N'InternalSignOff'));
GO
