-- Continues until replaced (Jeff, 27 Nov, D-121): after its end date the contract carries on under the same terms until a new
-- agreement replaces it (Medius's "Auto Extension - In Full"). It keeps its end date and doesn't renew by itself; past the end date
-- with no renewal decision it reads "Continuing until replaced", not overdue.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Contract ADD ContinuesUntilReplaced BIT NOT NULL CONSTRAINT DF_Contract_ContinuesUntilReplaced DEFAULT 0;
GO
ALTER TABLE contracts.Contract ADD CONSTRAINT CK_Contract_ContinuesNotEvergreenOrRenewing
    CHECK (ContinuesUntilReplaced = 0 OR (IsEvergreen = 0 AND AutoRenews = 0));
GO

-- On the contract form under Dates and notice, after the renewal term.
DECLARE @dates INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'dates'),
        @after INT = (SELECT Position FROM contracts.FieldDefinition WHERE [Key] = N'renewalTermMonths');
UPDATE contracts.FieldDefinition SET Position = Position + 1 WHERE SectionId = @dates AND Position > @after;
INSERT INTO contracts.FieldDefinition ([Key], IsBuiltIn, IsRuleField, Kind, Label, Help, SectionId, Position, IsRequired, ShowInList, Filterable) VALUES
    (N'continuesUntilReplaced', 1, 1, N'BuiltIn', N'Continues until replaced',
     N'After its end date it carries on under the same terms until a new agreement replaces it. It isn''t shown as overdue.', @dates, @after + 1, 0, 0, 1);
GO
