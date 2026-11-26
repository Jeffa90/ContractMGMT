-- Several sites, business units, entities and functions per contract (Jeff, 26 Nov, D-114). Each choice is a row here; the list item
-- says which list it belongs to. The single values contracts had are moved across, then the old columns go.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.ContractListValue (
    ContractId INT NOT NULL CONSTRAINT FK_ContractListValue_Contract REFERENCES contracts.Contract(Id),
    ListItemId INT NOT NULL CONSTRAINT FK_ContractListValue_Item REFERENCES contracts.ListItem(Id),
    CONSTRAINT PK_ContractListValue PRIMARY KEY (ContractId, ListItemId)
);
CREATE INDEX IX_ContractListValue_Item ON contracts.ContractListValue(ListItemId);
GO

INSERT INTO contracts.ContractListValue (ContractId, ListItemId)
SELECT Id, SiteId FROM contracts.Contract WHERE SiteId IS NOT NULL
UNION SELECT Id, BusinessUnitId FROM contracts.Contract WHERE BusinessUnitId IS NOT NULL
UNION SELECT Id, EntityId FROM contracts.Contract WHERE EntityId IS NOT NULL
UNION SELECT Id, FunctionId FROM contracts.Contract WHERE FunctionId IS NOT NULL;
GO

ALTER TABLE contracts.Contract DROP CONSTRAINT FK_Contract_Site;
ALTER TABLE contracts.Contract DROP CONSTRAINT FK_Contract_BusinessUnit;
ALTER TABLE contracts.Contract DROP CONSTRAINT FK_Contract_Entity;
ALTER TABLE contracts.Contract DROP CONSTRAINT FK_Contract_Function;
GO
ALTER TABLE contracts.Contract DROP COLUMN SiteId, BusinessUnitId, EntityId, FunctionId;
GO
