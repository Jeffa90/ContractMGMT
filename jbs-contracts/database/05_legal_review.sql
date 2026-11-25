-- Slice 3: Legal review as a back-and-forth between Legal and the contract's owner (product-spec section 5, decisions D-09, D-55 to D-58).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

-- Where a contract's Legal review is up to. Empty outside Legal review (the outcome stays once cleared).
ALTER TABLE contracts.Contract ADD
    WhoseTurn        NVARCHAR(10)   NULL CONSTRAINT CK_Contract_WhoseTurn CHECK (WhoseTurn IN (N'Legal', N'Owner')),
    LegalPersonId    INT            NULL CONSTRAINT FK_Contract_LegalPerson REFERENCES contracts.Person(Id),
    LegalStatus      NVARCHAR(30)   NULL CONSTRAINT CK_Contract_LegalStatus CHECK (LegalStatus IN (N'NotStarted', N'Reviewing', N'WaitingOnSupplier', N'WaitingOnOwner')),
    LegalNote        NVARCHAR(500)  NULL,
    TurnSinceUtc     DATETIME2(0)   NULL,
    LegalOutcome     NVARCHAR(2000) NULL,
    LegalClearedUtc  DATETIME2(0)   NULL,
    LegalClearedById INT            NULL CONSTRAINT FK_Contract_LegalClearedBy REFERENCES contracts.Person(Id);
GO
CREATE INDEX IX_Contract_Stage ON contracts.Contract(Stage) INCLUDE (WhoseTurn, LegalPersonId);
GO

-- The Legal conversation: every hand-over and status change, numbered per contract. Entries are never changed or deleted.
CREATE TABLE contracts.LegalTurn (
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LegalTurn PRIMARY KEY,
    ContractId    INT NOT NULL CONSTRAINT FK_LegalTurn_Contract REFERENCES contracts.Contract(Id),
    Number        INT NOT NULL,
    Action        NVARCHAR(30) NOT NULL CONSTRAINT CK_LegalTurn_Action CHECK (Action IN (N'SentToLegal', N'Taken', N'Assigned', N'StatusChanged', N'SentToOwner', N'Cleared')),
    ById          INT NOT NULL CONSTRAINT FK_LegalTurn_By REFERENCES contracts.Person(Id),
    AtUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_LegalTurn_AtUtc DEFAULT SYSUTCDATETIME(),
    WhoseTurn     NVARCHAR(10) NULL,     -- after this entry
    LegalPersonId INT NULL CONSTRAINT FK_LegalTurn_LegalPerson REFERENCES contracts.Person(Id),
    Status        NVARCHAR(30) NULL,
    Note          NVARCHAR(2000) NULL,
    CONSTRAINT UX_LegalTurn UNIQUE (ContractId, Number)
);
GO
-- Documents attached to an entry, as the version they were then.
CREATE TABLE contracts.LegalTurnDocument (
    LegalTurnId       INT NOT NULL CONSTRAINT FK_LegalTurnDocument_Turn REFERENCES contracts.LegalTurn(Id),
    DocumentVersionId INT NOT NULL CONSTRAINT FK_LegalTurnDocument_Version REFERENCES contracts.DocumentVersion(Id),
    CONSTRAINT PK_LegalTurnDocument PRIMARY KEY (LegalTurnId, DocumentVersionId)
);
GO
CREATE TRIGGER contracts.TR_LegalTurn_AppendOnly ON contracts.LegalTurn
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Legal conversation entries cannot be changed or deleted.', 1;
END;
GO
CREATE TRIGGER contracts.TR_LegalTurnDocument_AppendOnly ON contracts.LegalTurnDocument
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Legal conversation entries cannot be changed or deleted.', 1;
END;
GO
