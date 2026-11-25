-- JBS Contracts, slice 1: suppliers, lists, categories, contracts and documents.
-- Run once by scripts/ci/Invoke-SqlMigrations.ps1. Never edit after it has run; add a new numbered script.

CREATE TABLE contracts.Supplier (
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Supplier PRIMARY KEY,
    Name          NVARCHAR(200) NOT NULL,
    NameKey       AS UPPER(LTRIM(RTRIM(Name))) PERSISTED,
    Abn           NVARCHAR(11)  NULL,     -- 11 digits, spaces removed
    VendorNumber  NVARCHAR(50)  NULL,     -- SAP vendor number
    Notes         NVARCHAR(2000) NULL,
    IsActive      BIT NOT NULL CONSTRAINT DF_Supplier_IsActive DEFAULT 1,
    CreatedUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_Supplier_CreatedUtc DEFAULT SYSUTCDATETIME(),
    CreatedById   INT NULL CONSTRAINT FK_Supplier_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_Supplier_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedById   INT NULL CONSTRAINT FK_Supplier_UpdatedBy REFERENCES contracts.Person(Id),
    RowVersion    ROWVERSION NOT NULL
);
CREATE UNIQUE INDEX UX_Supplier_NameKey ON contracts.Supplier(NameKey);
GO

-- Categories, one level of subcategories (ParentId). Hidden, never deleted (contracts keep them).
CREATE TABLE contracts.Category (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
    ParentId   INT NULL CONSTRAINT FK_Category_Parent REFERENCES contracts.Category(Id),
    Name       NVARCHAR(100) NOT NULL,
    NameKey    AS UPPER(LTRIM(RTRIM(Name))) PERSISTED,
    SortOrder  INT NOT NULL CONSTRAINT DF_Category_SortOrder DEFAULT 0,
    IsActive   BIT NOT NULL CONSTRAINT DF_Category_IsActive DEFAULT 1,
    ParentKey  AS ISNULL(ParentId, 0) PERSISTED
);
CREATE UNIQUE INDEX UX_Category_Name ON contracts.Category(ParentKey, NameKey);
GO

-- Simple lists an admin manages: AgreementType, BusinessUnit, Site, Currency.
CREATE TABLE contracts.ListItem (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ListItem PRIMARY KEY,
    ListName   NVARCHAR(30)  NOT NULL CONSTRAINT CK_ListItem_ListName CHECK (ListName IN (N'AgreementType', N'BusinessUnit', N'Site', N'Currency')),
    Value      NVARCHAR(100) NOT NULL,
    ValueKey   AS UPPER(LTRIM(RTRIM(Value))) PERSISTED,
    SortOrder  INT NOT NULL CONSTRAINT DF_ListItem_SortOrder DEFAULT 0,
    IsActive   BIT NOT NULL CONSTRAINT DF_ListItem_IsActive DEFAULT 1
);
CREATE UNIQUE INDEX UX_ListItem_Value ON contracts.ListItem(ListName, ValueKey);
GO
-- Currencies only; JBS's own lists are added by an admin in the app.
INSERT INTO contracts.ListItem (ListName, Value, SortOrder) VALUES
    (N'Currency', N'AUD', 0), (N'Currency', N'USD', 1), (N'Currency', N'NZD', 2), (N'Currency', N'EUR', 3), (N'Currency', N'GBP', 4);
GO

-- Running numbers (JBSAUS-0001 ...). A plain table, not a SEQUENCE: the runtime account only has db_datareader and
-- db_datawriter, and NEXT VALUE FOR needs UPDATE permission on the sequence, which those roles don't give.
CREATE TABLE contracts.Counter (
    Name       NVARCHAR(50) NOT NULL CONSTRAINT PK_Counter PRIMARY KEY,
    NextValue  INT NOT NULL
);
INSERT INTO contracts.Counter (Name, NextValue) VALUES (N'ContractNumber', 1);
GO

CREATE TABLE contracts.Contract (
    Id                     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contract PRIMARY KEY,
    Reference              NVARCHAR(30)  NOT NULL,   -- JBSAUS-0001, set by the app
    Title                  NVARCHAR(200) NOT NULL,
    Description            NVARCHAR(4000) NULL,
    SupplierId             INT NULL CONSTRAINT FK_Contract_Supplier REFERENCES contracts.Supplier(Id),
    CategoryId             INT NULL CONSTRAINT FK_Contract_Category REFERENCES contracts.Category(Id),
    AgreementTypeId        INT NULL CONSTRAINT FK_Contract_AgreementType REFERENCES contracts.ListItem(Id),
    BusinessUnitId         INT NULL CONSTRAINT FK_Contract_BusinessUnit REFERENCES contracts.ListItem(Id),
    SiteId                 INT NULL CONSTRAINT FK_Contract_Site REFERENCES contracts.ListItem(Id),
    OwnerId                INT NOT NULL CONSTRAINT FK_Contract_Owner REFERENCES contracts.Person(Id),
    BackupOwnerId          INT NULL CONSTRAINT FK_Contract_BackupOwner REFERENCES contracts.Person(Id),
    IsConfidential         BIT NOT NULL CONSTRAINT DF_Contract_IsConfidential DEFAULT 0,
    Value                  DECIMAL(19,4) NULL CONSTRAINT CK_Contract_Value CHECK (Value IS NULL OR Value >= 0),
    CurrencyCode           NVARCHAR(3) NOT NULL CONSTRAINT DF_Contract_Currency DEFAULT N'AUD',
    SapOutlineAgreement    NVARCHAR(50) NULL,
    StartDate              DATE NULL,
    EffectiveDate          DATE NULL,
    EndDate                DATE NULL,
    IsEvergreen            BIT NOT NULL CONSTRAINT DF_Contract_IsEvergreen DEFAULT 0,
    NoticePeriodDays       INT NULL CONSTRAINT CK_Contract_Notice CHECK (NoticePeriodDays IS NULL OR NoticePeriodDays BETWEEN 0 AND 3650),
    ReviewDate             DATE NULL,
    NeedsLegalReview       BIT NOT NULL CONSTRAINT DF_Contract_NeedsLegal DEFAULT 1,
    Stage                  NVARCHAR(30) NOT NULL CONSTRAINT DF_Contract_Stage DEFAULT N'Draft'
        CONSTRAINT CK_Contract_Stage CHECK (Stage IN (N'Draft', N'LegalReview', N'Signing', N'Approval', N'Effective', N'Ended', N'Archived', N'Withdrawn')),
    StageSinceUtc          DATETIME2(0) NOT NULL CONSTRAINT DF_Contract_StageSince DEFAULT SYSUTCDATETIME(),
    IsLocked               BIT NOT NULL CONSTRAINT DF_Contract_IsLocked DEFAULT 0,
    WithdrawnReason        NVARCHAR(1000) NULL,
    CreatedUtc             DATETIME2(0) NOT NULL CONSTRAINT DF_Contract_CreatedUtc DEFAULT SYSUTCDATETIME(),
    CreatedById            INT NOT NULL CONSTRAINT FK_Contract_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc             DATETIME2(0) NOT NULL CONSTRAINT DF_Contract_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedById            INT NOT NULL CONSTRAINT FK_Contract_UpdatedBy REFERENCES contracts.Person(Id),
    RowVersion             ROWVERSION NOT NULL,
    CONSTRAINT CK_Contract_Evergreen CHECK (IsEvergreen = 0 OR EndDate IS NULL),
    CONSTRAINT CK_Contract_Dates CHECK (EndDate IS NULL OR StartDate IS NULL OR EndDate >= StartDate)
);
CREATE UNIQUE INDEX UX_Contract_Reference ON contracts.Contract(Reference);
CREATE INDEX IX_Contract_Owner ON contracts.Contract(OwnerId) INCLUDE (Stage);
CREATE INDEX IX_Contract_Backup ON contracts.Contract(BackupOwnerId) INCLUDE (Stage);
CREATE INDEX IX_Contract_Supplier ON contracts.Contract(SupplierId);
GO

-- A document on a contract; each upload of a new version adds a DocumentVersion. Files live in the data folder, never in the database.
CREATE TABLE contracts.Document (
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
    ContractId      INT NOT NULL CONSTRAINT FK_Document_Contract REFERENCES contracts.Contract(Id),
    Kind            NVARCHAR(30) NOT NULL CONSTRAINT CK_Document_Kind CHECK (Kind IN (N'Draft', N'FinalAgreement', N'SignedCopy', N'Schedule', N'LegalMarkup', N'Variation', N'Correspondence', N'Supporting')),
    Title           NVARCHAR(200) NOT NULL,
    CurrentVersion  INT NOT NULL CONSTRAINT DF_Document_CurrentVersion DEFAULT 1,
    IsRemoved       BIT NOT NULL CONSTRAINT DF_Document_IsRemoved DEFAULT 0,   -- hidden from the contract, file and history kept
    CreatedUtc      DATETIME2(0) NOT NULL CONSTRAINT DF_Document_CreatedUtc DEFAULT SYSUTCDATETIME(),
    CreatedById     INT NOT NULL CONSTRAINT FK_Document_CreatedBy REFERENCES contracts.Person(Id)
);
CREATE INDEX IX_Document_Contract ON contracts.Document(ContractId);
GO
CREATE TABLE contracts.DocumentVersion (
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentVersion PRIMARY KEY,
    DocumentId    INT NOT NULL CONSTRAINT FK_DocumentVersion_Document REFERENCES contracts.Document(Id),
    VersionNumber INT NOT NULL,
    FileName      NVARCHAR(255) NOT NULL,
    ContentType   NVARCHAR(100) NOT NULL,
    SizeBytes     BIGINT NOT NULL,
    Sha256        CHAR(64) NOT NULL,
    StoredName    NVARCHAR(100) NOT NULL,   -- yyyy/guid.ext under the documents folder; never built from the uploaded name
    UploadedUtc   DATETIME2(0) NOT NULL CONSTRAINT DF_DocumentVersion_UploadedUtc DEFAULT SYSUTCDATETIME(),
    UploadedById  INT NOT NULL CONSTRAINT FK_DocumentVersion_UploadedBy REFERENCES contracts.Person(Id),
    CONSTRAINT UX_DocumentVersion UNIQUE (DocumentId, VersionNumber)
);
GO
