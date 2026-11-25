-- Slice 5: Home. Each person's choice of tiles ("Choose what I see", product-spec section 12).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.HomePreference (
    PersonId    INT NOT NULL CONSTRAINT PK_HomePreference PRIMARY KEY CONSTRAINT FK_HomePreference_Person REFERENCES contracts.Person(Id),
    HiddenTiles NVARCHAR(1000) NOT NULL CONSTRAINT DF_HomePreference_Hidden DEFAULT N'[]',   -- JSON array of tile keys
    FirstGroup  NVARCHAR(10) NOT NULL CONSTRAINT DF_HomePreference_First DEFAULT N'turn'
        CONSTRAINT CK_HomePreference_First CHECK (FirstGroup IN (N'turn', N'coming')),
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_HomePreference_Updated DEFAULT SYSUTCDATETIME()
);
GO
