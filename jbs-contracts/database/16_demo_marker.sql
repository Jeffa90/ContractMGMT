-- Demo data marker (D-97): demo contracts and suppliers are marked, so they never send email and "Refresh demo data" can
-- archive the old set before loading a fresh one. Production never has demo data (AllowDemoData is always off there).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Contract ADD IsDemo BIT NOT NULL CONSTRAINT DF_Contract_IsDemo DEFAULT 0;
ALTER TABLE contracts.Supplier ADD IsDemo BIT NOT NULL CONSTRAINT DF_Supplier_IsDemo DEFAULT 0;
GO

-- Demo data loaded before this script (D-66): the contracts it added (and renewals started from them), and its suppliers.
UPDATE contracts.Contract SET IsDemo = 1
WHERE Id IN (SELECT SubjectId FROM contracts.AuditEvent WHERE Action = N'demo.contract' AND SubjectType = N'Contract');
UPDATE k SET IsDemo = 1 FROM contracts.Contract k JOIN contracts.Contract o ON o.Id = k.RenewsContractId WHERE o.IsDemo = 1;
UPDATE contracts.Supplier SET IsDemo = 1 WHERE Notes = N'Fictional demo supplier.';
GO
