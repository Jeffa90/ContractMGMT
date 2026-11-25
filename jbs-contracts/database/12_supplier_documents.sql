-- Supplier documents (D-88): a supplier's own documents (insurance, certificates, NDAs, business reviews and so on), with versions,
-- an optional expiry date and an opt-in reminder before it expires. Uploaded by people ticked Procurement (and admins); seen by everyone.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

-- A new tick on People: Procurement.
ALTER TABLE contracts.Person ADD IsProcurement BIT NOT NULL CONSTRAINT DF_Person_IsProcurement DEFAULT 0;
GO

-- The kinds of supplier document: a list admins add to (Lists page). A starting set; admins hide or add as they like.
ALTER TABLE contracts.ListItem DROP CONSTRAINT CK_ListItem_ListName;
GO
ALTER TABLE contracts.ListItem ADD CONSTRAINT CK_ListItem_ListName
    CHECK (ListName IN (N'AgreementType', N'BusinessUnit', N'Site', N'Currency', N'SupplierDocumentType'));
GO
INSERT INTO contracts.ListItem (ListName, Value, SortOrder) VALUES
    (N'SupplierDocumentType', N'Insurance certificate', 1),
    (N'SupplierDocumentType', N'Food safety or quality certificate', 2),
    (N'SupplierDocumentType', N'Safety (WHS) document', 3),
    (N'SupplierDocumentType', N'Modern slavery statement', 4),
    (N'SupplierDocumentType', N'Prequalification or onboarding', 5),
    (N'SupplierDocumentType', N'Non-disclosure agreement', 6),
    (N'SupplierDocumentType', N'Business review', 7),
    (N'SupplierDocumentType', N'Other', 99);
GO

CREATE TABLE contracts.SupplierDocument (
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierDocument PRIMARY KEY,
    SupplierId     INT NOT NULL CONSTRAINT FK_SupplierDocument_Supplier REFERENCES contracts.Supplier(Id),
    TypeId         INT NOT NULL CONSTRAINT FK_SupplierDocument_Type REFERENCES contracts.ListItem(Id),
    Title          NVARCHAR(200) NOT NULL,
    ExpiresOn      DATE NULL,
    CurrentVersion INT NOT NULL CONSTRAINT DF_SupplierDocument_CurrentVersion DEFAULT 1,
    IsRemoved      BIT NOT NULL CONSTRAINT DF_SupplierDocument_IsRemoved DEFAULT 0,   -- hidden from the supplier; file and history kept
    CreatedUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierDocument_Created DEFAULT SYSUTCDATETIME(),
    CreatedById    INT NOT NULL CONSTRAINT FK_SupplierDocument_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierDocument_Updated DEFAULT SYSUTCDATETIME(),
    RowVersion     ROWVERSION NOT NULL
);
CREATE INDEX IX_SupplierDocument_Supplier ON contracts.SupplierDocument(SupplierId);
GO
CREATE TABLE contracts.SupplierDocumentVersion (
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierDocumentVersion PRIMARY KEY,
    DocumentId    INT NOT NULL CONSTRAINT FK_SupplierDocumentVersion_Document REFERENCES contracts.SupplierDocument(Id),
    VersionNumber INT NOT NULL,
    FileName      NVARCHAR(255) NOT NULL,
    ContentType   NVARCHAR(100) NOT NULL,
    SizeBytes     BIGINT NOT NULL,
    Sha256        CHAR(64) NOT NULL,
    StoredName    NVARCHAR(100) NOT NULL,   -- yyyy/guid.ext under the documents folder; never built from the uploaded name
    UploadedUtc   DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierDocumentVersion_Uploaded DEFAULT SYSUTCDATETIME(),
    UploadedById  INT NOT NULL CONSTRAINT FK_SupplierDocumentVersion_UploadedBy REFERENCES contracts.Person(Id),
    CONSTRAINT UX_SupplierDocumentVersion UNIQUE (DocumentId, VersionNumber)
);
GO

-- "Remind me before it expires": off unless someone ticks it; each person chooses for themselves.
CREATE TABLE contracts.SupplierDocumentWatch (
    DocumentId INT NOT NULL CONSTRAINT FK_SupplierDocumentWatch_Document REFERENCES contracts.SupplierDocument(Id),
    PersonId   INT NOT NULL CONSTRAINT FK_SupplierDocumentWatch_Person REFERENCES contracts.Person(Id),
    CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierDocumentWatch_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_SupplierDocumentWatch PRIMARY KEY (DocumentId, PersonId)
);
-- Expiry reminders sent, so each step goes once even after a restart.
CREATE TABLE contracts.SupplierDocumentReminder (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierDocumentReminder PRIMARY KEY,
    DocumentId INT NOT NULL CONSTRAINT FK_SupplierDocumentReminder_Document REFERENCES contracts.SupplierDocument(Id),
    PersonId   INT NOT NULL CONSTRAINT FK_SupplierDocumentReminder_Person REFERENCES contracts.Person(Id),
    ExpiresOn  DATE NOT NULL,
    DaysBefore INT NOT NULL,
    SentUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierDocumentReminder_Sent DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UX_SupplierDocumentReminder UNIQUE (DocumentId, PersonId, ExpiresOn, DaysBefore)
);
GO

-- The expiry reminder's days and wording, editable in Emails and reminders like the others.
ALTER TABLE contracts.ReminderSetting DROP CONSTRAINT CK_ReminderSetting_Kind;
GO
ALTER TABLE contracts.ReminderSetting ADD CONSTRAINT CK_ReminderSetting_Kind CHECK (Kind IN (N'Notice', N'Escalation', N'End', N'Review', N'SupplierExpiry'));
GO
INSERT INTO contracts.ReminderSetting (Kind, Days) VALUES (N'SupplierExpiry', N'60,30');
GO
ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack',
    N'HandedOver', N'VariationCleared', N'VariationWaiting', N'VariationApproved', N'VariationSentBack',
    N'SupplierExpiry'));
GO
DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'SupplierExpiry', N'{Supplier}: {Document} expires in {Days} days',
     N'Hi {First name},' + @p +
     N'{Supplier}''s {Document type} "{Document}" expires on {Due date}, in {Days} days.' + @p +
     N'Ask the supplier for a current one, then add it on the supplier''s page as a new version with its new expiry date.' + @p +
     N'You''re getting this because you ticked Remind me before it expires. Untick it on the supplier''s page to stop.');
GO
