-- Admins add their own contract document kinds and supplier risk levels under Admin, Lists (D-98). The built-in ones stay as they
-- are (the app relies on them) and aren't in the lists. A document or supplier using an added one stores L followed by the item's
-- Id (for example L42), so renaming the item renames it everywhere.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.ListItem DROP CONSTRAINT CK_ListItem_ListName;
GO
ALTER TABLE contracts.ListItem ADD CONSTRAINT CK_ListItem_ListName
    CHECK (ListName IN (N'AgreementType', N'BusinessUnit', N'Site', N'Currency', N'SupplierDocumentType', N'SupplierTier', N'DocumentKind', N'SupplierRisk'));
GO

ALTER TABLE contracts.Document DROP CONSTRAINT CK_Document_Kind;
GO
ALTER TABLE contracts.Document ADD CONSTRAINT CK_Document_Kind
    CHECK (Kind IN (N'Draft', N'FinalAgreement', N'SignedCopy', N'Schedule', N'LegalMarkup', N'Variation', N'Correspondence', N'Supporting', N'InternalSignOff')
           OR (Kind LIKE N'L[0-9]%' AND TRY_CAST(SUBSTRING(Kind, 2, 29) AS INT) IS NOT NULL));
GO

ALTER TABLE contracts.Supplier DROP CONSTRAINT CK_Supplier_RiskRating;
GO
ALTER TABLE contracts.Supplier ADD CONSTRAINT CK_Supplier_RiskRating
    CHECK (RiskRating IN (N'Low', N'Medium', N'High') OR (RiskRating LIKE N'L[0-9]%' AND TRY_CAST(SUBSTRING(RiskRating, 2, 9) AS INT) IS NOT NULL));
GO
