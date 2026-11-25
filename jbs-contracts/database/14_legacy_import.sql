-- Slice 9: importing old contracts (D-91). Each imported contract keeps its old reference (for example its Medius agreement number), so its
-- files can be matched and a second run skips it. The column matching an admin chose is remembered for next time.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Contract ADD
    LegacyReference NVARCHAR(100) NULL,      -- the reference in the old system
    ImportedUtc     DATETIME2(0)  NULL,
    ImportedFrom    NVARCHAR(260) NULL;      -- the file it came from
GO
CREATE UNIQUE INDEX UX_Contract_LegacyReference ON contracts.Contract(LegacyReference) WHERE LegacyReference IS NOT NULL;
GO

CREATE TABLE contracts.ImportMapping (
    Name        NVARCHAR(30) NOT NULL CONSTRAINT PK_ImportMapping PRIMARY KEY,   -- 'legacy'
    MappingJson NVARCHAR(MAX) NOT NULL,      -- source column (normalised) to field key
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_ImportMapping_Updated DEFAULT SYSUTCDATETIME(),
    UpdatedById INT NOT NULL CONSTRAINT FK_ImportMapping_By REFERENCES contracts.Person(Id)
);
GO
