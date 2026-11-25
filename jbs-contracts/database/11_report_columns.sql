-- Slice 8: each person's choice of columns on a report (the Register's "Columns", D-86).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.ReportPreference (
    PersonId    INT NOT NULL CONSTRAINT FK_ReportPreference_Person REFERENCES contracts.Person(Id),
    Report      NVARCHAR(30) NOT NULL CONSTRAINT CK_ReportPreference_Report CHECK (Report IN (N'register')),
    ColumnsJson NVARCHAR(4000) NOT NULL,                 -- JSON array of column keys, in order
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_ReportPreference_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ReportPreference PRIMARY KEY (PersonId, Report)
);
GO
