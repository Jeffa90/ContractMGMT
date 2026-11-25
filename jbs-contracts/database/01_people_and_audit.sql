-- JBS Contracts, slice 0: people and the audit trail.
-- Run once by scripts/ci/Invoke-SqlMigrations.ps1 (recorded in dbo.SchemaVersions). Never edit after it has run; add a new numbered script.

IF SCHEMA_ID(N'contracts') IS NULL
    EXEC(N'CREATE SCHEMA contracts AUTHORIZATION dbo');
GO

-- Everyone who can use the app. An admin adds them; nobody is ever deleted (IsActive = 0 instead).
CREATE TABLE contracts.Person (
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Person PRIMARY KEY,
    WindowsAccount  NVARCHAR(256)  NOT NULL,   -- DOMAIN\user, as shown to people
    AccountKey      AS UPPER(WindowsAccount) PERSISTED, -- case-insensitive match whatever the collation
    Name            NVARCHAR(200)  NOT NULL,
    Email           NVARCHAR(320)  NOT NULL,
    IsContractOwner BIT NOT NULL CONSTRAINT DF_Person_IsContractOwner DEFAULT 0,
    IsLegal         BIT NOT NULL CONSTRAINT DF_Person_IsLegal DEFAULT 0,
    IsApprover      BIT NOT NULL CONSTRAINT DF_Person_IsApprover DEFAULT 0,
    IsGroupHead     BIT NOT NULL CONSTRAINT DF_Person_IsGroupHead DEFAULT 0,
    IsAdmin         BIT NOT NULL CONSTRAINT DF_Person_IsAdmin DEFAULT 0,
    IsAuditor       BIT NOT NULL CONSTRAINT DF_Person_IsAuditor DEFAULT 0,
    IsActive        BIT NOT NULL CONSTRAINT DF_Person_IsActive DEFAULT 1,
    LastSignInUtc   DATETIME2(0) NULL,
    CreatedUtc      DATETIME2(0) NOT NULL CONSTRAINT DF_Person_CreatedUtc DEFAULT SYSUTCDATETIME(),
    CreatedById     INT NULL CONSTRAINT FK_Person_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc      DATETIME2(0) NOT NULL CONSTRAINT DF_Person_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedById     INT NULL CONSTRAINT FK_Person_UpdatedBy REFERENCES contracts.Person(Id),
    RowVersion      ROWVERSION NOT NULL,
    CONSTRAINT CK_Person_GroupHeadIsApprover CHECK (IsGroupHead = 0 OR IsApprover = 1)
);
GO
CREATE UNIQUE INDEX UX_Person_AccountKey ON contracts.Person(AccountKey);
-- At most one Group Head at a time; the other approvers are backups.
CREATE UNIQUE INDEX UX_Person_OneGroupHead ON contracts.Person(IsGroupHead) WHERE IsGroupHead = 1;
GO

-- Append-only audit trail. Each event carries the previous event's hash, so a changed or missing event shows.
CREATE TABLE contracts.AuditEvent (
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditEvent PRIMARY KEY,
    OccurredUtc   DATETIME2(3)   NOT NULL,
    PersonId      INT            NULL CONSTRAINT FK_AuditEvent_Person REFERENCES contracts.Person(Id),
    ActorName     NVARCHAR(200)  NOT NULL,   -- kept as it was at the time
    Action        NVARCHAR(100)  NOT NULL,   -- stable code, e.g. person.added
    SubjectType   NVARCHAR(50)   NOT NULL,   -- e.g. Person, Contract
    SubjectId     INT            NULL,
    ContractId    INT            NULL,
    Summary       NVARCHAR(1000) NOT NULL,   -- plain sentence shown in Activity
    DetailsJson   NVARCHAR(MAX)  NULL,       -- old and new values
    PreviousHash  CHAR(64)       NOT NULL,
    Hash          CHAR(64)       NOT NULL
);
GO
CREATE INDEX IX_AuditEvent_Subject ON contracts.AuditEvent(SubjectType, SubjectId, Id);
CREATE INDEX IX_AuditEvent_Contract ON contracts.AuditEvent(ContractId, Id) WHERE ContractId IS NOT NULL;
GO
-- The app's account can insert audit events but never change or remove them.
CREATE TRIGGER contracts.TR_AuditEvent_AppendOnly ON contracts.AuditEvent
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Audit events cannot be changed or deleted.', 1;
END;
GO
