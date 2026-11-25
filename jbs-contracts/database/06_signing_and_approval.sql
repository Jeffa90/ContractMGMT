-- Slice 4: signing, the Group Head's confirmation (approval), registering and locking (product-spec sections 4 and 6; D-59 to D-62).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

-- When the contract was registered (approved into the register) and by whom. Set by the app, never typed.
ALTER TABLE contracts.Contract ADD
    RegisteredUtc     DATETIME2(0) NULL,
    RegisteredById    INT          NULL CONSTRAINT FK_Contract_RegisteredBy REFERENCES contracts.Person(Id),
    RegisteredAsBackup BIT         NULL;
GO

-- Every approval decision: approved and registered, or sent back with a reason. Never changed or deleted.
CREATE TABLE contracts.Approval (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Approval PRIMARY KEY,
    ContractId  INT NOT NULL CONSTRAINT FK_Approval_Contract REFERENCES contracts.Contract(Id),
    Decision    NVARCHAR(20) NOT NULL CONSTRAINT CK_Approval_Decision CHECK (Decision IN (N'Approved', N'SentBack')),
    ById        INT NOT NULL CONSTRAINT FK_Approval_By REFERENCES contracts.Person(Id),
    AsBackupFor INT NULL CONSTRAINT FK_Approval_AsBackupFor REFERENCES contracts.Person(Id),   -- the Group Head at the time, when a backup acted
    AtUtc       DATETIME2(0) NOT NULL CONSTRAINT DF_Approval_AtUtc DEFAULT SYSUTCDATETIME(),
    Reason      NVARCHAR(2000) NULL
);
CREATE INDEX IX_Approval_Contract ON contracts.Approval(ContractId);
GO
CREATE TRIGGER contracts.TR_Approval_AppendOnly ON contracts.Approval
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Approval decisions cannot be changed or deleted.', 1;
END;
GO
