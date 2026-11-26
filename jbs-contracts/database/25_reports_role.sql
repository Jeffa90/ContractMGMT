-- A Reports tick on People (Ryan's feedback, D-110): people ticked see Reports (Register, Pipeline, Approvals) for the contracts they
-- may see. The audit trail and Download everything stay with admins and auditors.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Person ADD IsReports BIT NOT NULL CONSTRAINT DF_Person_IsReports DEFAULT 0;
GO
