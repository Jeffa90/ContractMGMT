-- Standard fields agreed with Jeff after comparing Medius's contract fields (D-102): Entity and Function (drop-downs an admin keeps
-- under Admin, Lists) and auto-renewal (Renews automatically, with a renewal term in months: when the end date passes and no one
-- has decided to let it end, the daily job moves the end date on by the term).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.ListItem DROP CONSTRAINT CK_ListItem_ListName;
GO
ALTER TABLE contracts.ListItem ADD CONSTRAINT CK_ListItem_ListName
    CHECK (ListName IN (N'AgreementType', N'BusinessUnit', N'Site', N'Currency', N'SupplierDocumentType', N'SupplierTier', N'DocumentKind', N'SupplierRisk',
                        N'Entity', N'Function'));
GO

ALTER TABLE contracts.Contract ADD
    EntityId          INT NULL CONSTRAINT FK_Contract_Entity REFERENCES contracts.ListItem(Id),
    FunctionId        INT NULL CONSTRAINT FK_Contract_Function REFERENCES contracts.ListItem(Id),
    AutoRenews        BIT NOT NULL CONSTRAINT DF_Contract_AutoRenews DEFAULT 0,
    RenewalTermMonths INT NULL CONSTRAINT CK_Contract_RenewalTermMonths CHECK (RenewalTermMonths BETWEEN 1 AND 120);
GO
ALTER TABLE contracts.Contract ADD CONSTRAINT CK_Contract_AutoRenewsNotEvergreen CHECK (AutoRenews = 0 OR IsEvergreen = 0);
GO

-- The new fields on the contract form: Entity and Function under Owner and access, auto-renewal under Dates and notice.
DECLARE @owner INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'owner'),
        @dates INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'dates');
DECLARE @ownerNext INT = (SELECT ISNULL(MAX(Position), -1) + 1 FROM contracts.FieldDefinition WHERE SectionId = @owner),
        @evergreen INT = (SELECT Position FROM contracts.FieldDefinition WHERE [Key] = N'evergreen');
-- Room after Evergreen for the two auto-renewal fields.
UPDATE contracts.FieldDefinition SET Position = Position + 2 WHERE SectionId = @dates AND Position > @evergreen;
INSERT INTO contracts.FieldDefinition ([Key], IsBuiltIn, IsRuleField, Kind, Label, Help, SectionId, Position, IsRequired, ShowInList, Filterable) VALUES
    (N'entity',            1, 0, N'BuiltIn', N'Entity', N'The JBS company that signs the contract.', @owner, @ownerNext, 0, 0, 1),
    (N'function',          1, 0, N'BuiltIn', N'Function', N'The part of the business the contract is for.', @owner, @ownerNext + 1, 0, 0, 1),
    (N'autoRenews',        1, 1, N'BuiltIn', N'Renews automatically', N'If no one gives notice, it carries on for another term and the end date moves on.', @dates, @evergreen + 1, 0, 0, 1),
    (N'renewalTermMonths', 1, 1, N'BuiltIn', N'Renewal term (months)', N'How long each automatic renewal lasts, for example 12.', @dates, @evergreen + 2, 0, 0, 0);
GO
