-- JBS Contracts, slice 2: contract fields and sections an admin arranges without code (product-spec section 10, D-15 to D-18).
-- Built-in fields are listed here too, so they can be renamed, moved, hidden (except rule fields) and required.
-- No database change is ever needed for a new field: values live in ContractFieldValue. Never edit after it has run.

CREATE TABLE contracts.FormSection (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FormSection PRIMARY KEY,
    [Key]      NVARCHAR(60)  NOT NULL CONSTRAINT UX_FormSection_Key UNIQUE,   -- stable, never reused
    Title      NVARCHAR(100) NOT NULL,
    Position   INT NOT NULL,
    IsHidden   BIT NOT NULL CONSTRAINT DF_FormSection_IsHidden DEFAULT 0
);
GO

CREATE TABLE contracts.FieldDefinition (
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldDefinition PRIMARY KEY,
    [Key]          NVARCHAR(60)   NOT NULL CONSTRAINT UX_FieldDefinition_Key UNIQUE,   -- stable, never reused
    IsBuiltIn      BIT            NOT NULL,
    IsRuleField    BIT            NOT NULL CONSTRAINT DF_FieldDefinition_IsRuleField DEFAULT 0,  -- can be renamed and moved, never hidden
    Kind           NVARCHAR(30)   NOT NULL CONSTRAINT CK_FieldDefinition_Kind CHECK (Kind IN (
                       N'ShortText', N'LongText', N'WholeNumber', N'Decimal', N'Money', N'Date', N'YesNo',
                       N'Choice', N'MultiChoice', N'Person', N'Supplier', N'Link', N'Email', N'BuiltIn')),
    Label          NVARCHAR(100)  NOT NULL,
    Help           NVARCHAR(300)  NULL,
    SectionId      INT            NOT NULL CONSTRAINT FK_FieldDefinition_Section REFERENCES contracts.FormSection(Id),
    Position       INT            NOT NULL,
    IsRequired     BIT            NOT NULL CONSTRAINT DF_FieldDefinition_IsRequired DEFAULT 0,  -- checked when a contract leaves Draft
    IsHidden       BIT            NOT NULL CONSTRAINT DF_FieldDefinition_IsHidden DEFAULT 0,   -- "removed": values kept
    ShowInList     BIT            NOT NULL CONSTRAINT DF_FieldDefinition_ShowInList DEFAULT 0, -- offered as a Contracts list column
    Filterable     BIT            NOT NULL CONSTRAINT DF_FieldDefinition_Filterable DEFAULT 0,
    InExport       BIT            NOT NULL CONSTRAINT DF_FieldDefinition_InExport DEFAULT 1,
    AfterEffective NVARCHAR(20)   NOT NULL CONSTRAINT DF_FieldDefinition_AfterEffective DEFAULT N'Variation'
                       CONSTRAINT CK_FieldDefinition_AfterEffective CHECK (AfterEffective IN (N'Variation', N'AnyTime')),
    ChoicesJson    NVARCHAR(MAX)  NULL,   -- [{ "key": "...", "label": "...", "hidden": false }]
    MaxLength      INT            NULL,
    MinValue       DECIMAL(19,4)  NULL,
    MaxValue       DECIMAL(19,4)  NULL,
    CONSTRAINT CK_FieldDefinition_RuleNotHidden CHECK (IsRuleField = 0 OR IsHidden = 0)
);
GO

CREATE TABLE contracts.ContractFieldValue (
    ContractId   INT NOT NULL CONSTRAINT FK_ContractFieldValue_Contract REFERENCES contracts.Contract(Id),
    FieldId      INT NOT NULL CONSTRAINT FK_ContractFieldValue_Field REFERENCES contracts.FieldDefinition(Id),
    TextValue    NVARCHAR(4000) NULL,     -- text, link, email, choice key, or a JSON array of keys for MultiChoice
    NumberValue  DECIMAL(19,4)  NULL,
    DateValue    DATE           NULL,
    BoolValue    BIT            NULL,
    PersonId     INT            NULL CONSTRAINT FK_ContractFieldValue_Person REFERENCES contracts.Person(Id),
    SupplierId   INT            NULL CONSTRAINT FK_ContractFieldValue_Supplier REFERENCES contracts.Supplier(Id),
    UpdatedUtc   DATETIME2(0)   NOT NULL CONSTRAINT DF_ContractFieldValue_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ContractFieldValue PRIMARY KEY (ContractId, FieldId)
);
CREATE INDEX IX_ContractFieldValue_Field ON contracts.ContractFieldValue(FieldId);
GO

-- Every saved layout, for History and "put back".
CREATE TABLE contracts.LayoutVersion (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LayoutVersion PRIMARY KEY,
    SavedUtc    DATETIME2(0)  NOT NULL CONSTRAINT DF_LayoutVersion_SavedUtc DEFAULT SYSUTCDATETIME(),
    SavedById   INT           NULL CONSTRAINT FK_LayoutVersion_Person REFERENCES contracts.Person(Id),
    SavedByName NVARCHAR(200) NOT NULL,
    Summary     NVARCHAR(2000) NOT NULL,
    LayoutJson  NVARCHAR(MAX) NOT NULL
);
GO

-- The starting layout: the built-in fields in their sections.
INSERT INTO contracts.FormSection ([Key], Title, Position) VALUES
    (N'basics', N'Basics', 0), (N'owner', N'Owner and access', 1), (N'value', N'Value', 2), (N'dates', N'Dates and notice', 3), (N'legal', N'Legal', 4);
GO
DECLARE @basics INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'basics'),
        @owner  INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'owner'),
        @value  INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'value'),
        @dates  INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'dates'),
        @legal  INT = (SELECT Id FROM contracts.FormSection WHERE [Key] = N'legal');
INSERT INTO contracts.FieldDefinition ([Key], IsBuiltIn, IsRuleField, Kind, Label, Help, SectionId, Position, IsRequired, ShowInList, Filterable) VALUES
    (N'title',               1, 1, N'BuiltIn', N'Title', N'For example "Freight services 2026".', @basics, 0, 1, 1, 0),
    (N'supplier',            1, 1, N'BuiltIn', N'Supplier', NULL, @basics, 1, 1, 1, 1),
    (N'category',            1, 1, N'BuiltIn', N'Category', NULL, @basics, 2, 1, 1, 1),
    (N'agreementType',       1, 0, N'BuiltIn', N'Agreement type', NULL, @basics, 3, 0, 0, 1),
    (N'description',         1, 0, N'BuiltIn', N'Description', NULL, @basics, 4, 0, 0, 0),
    (N'owner',               1, 1, N'BuiltIn', N'Owner', NULL, @owner, 0, 1, 1, 1),
    (N'backupOwner',         1, 0, N'BuiltIn', N'Backup owner', NULL, @owner, 1, 0, 0, 0),
    (N'confidential',        1, 1, N'BuiltIn', N'Confidential', N'Only the owner and backup, Legal, approvers, admins and auditors can see it.', @owner, 2, 0, 0, 1),
    (N'businessUnit',        1, 0, N'BuiltIn', N'Business unit', NULL, @owner, 3, 0, 0, 1),
    (N'site',                1, 0, N'BuiltIn', N'Site', NULL, @owner, 4, 0, 0, 1),
    (N'value',               1, 0, N'BuiltIn', N'Value', N'The estimated total value.', @value, 0, 0, 0, 0),
    (N'sapOutlineAgreement', 1, 0, N'BuiltIn', N'SAP outline agreement number', NULL, @value, 1, 0, 0, 0),
    (N'startDate',           1, 0, N'BuiltIn', N'Start date', NULL, @dates, 0, 0, 0, 0),
    (N'effectiveDate',       1, 1, N'BuiltIn', N'Effective date', NULL, @dates, 1, 0, 0, 0),
    (N'endDate',             1, 1, N'BuiltIn', N'End date', NULL, @dates, 2, 0, 0, 0),
    (N'evergreen',           1, 1, N'BuiltIn', N'Evergreen (no end date)', N'It carries on until someone ends it.', @dates, 3, 0, 0, 0),
    (N'noticePeriodDays',    1, 1, N'BuiltIn', N'Notice period (days)', N'How many days'' notice must be given before it ends or renews.', @dates, 4, 0, 0, 0),
    (N'reviewDate',          1, 0, N'BuiltIn', N'Review date', NULL, @dates, 5, 0, 0, 0),
    (N'needsLegalReview',    1, 1, N'BuiltIn', N'Needs Legal review', N'On by default. Untick only if Legal has agreed it doesn''t need a review.', @legal, 0, 0, 0, 1);
GO
