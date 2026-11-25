-- Supplier relationships (D-90): a relationship owner, tier, risk rating, contacts, the next business review due, and a structured log of
-- business reviews (date, attendees, summary, rating and scores, actions with owners and due dates; the deck is a supplier document).
-- Details are edited by Procurement and admins; reviews also by the supplier's relationship owner. Everyone signed in reads them.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.ListItem DROP CONSTRAINT CK_ListItem_ListName;
GO
ALTER TABLE contracts.ListItem ADD CONSTRAINT CK_ListItem_ListName
    CHECK (ListName IN (N'AgreementType', N'BusinessUnit', N'Site', N'Currency', N'SupplierDocumentType', N'SupplierTier'));
GO
INSERT INTO contracts.ListItem (ListName, Value, SortOrder) VALUES
    (N'SupplierTier', N'Strategic', 1), (N'SupplierTier', N'Preferred', 2), (N'SupplierTier', N'Approved', 3), (N'SupplierTier', N'Transactional', 4);
GO

ALTER TABLE contracts.Supplier ADD
    RelationshipOwnerId INT NULL CONSTRAINT FK_Supplier_RelationshipOwner REFERENCES contracts.Person(Id),
    TierId              INT NULL CONSTRAINT FK_Supplier_Tier REFERENCES contracts.ListItem(Id),
    RiskRating          NVARCHAR(10) NULL CONSTRAINT CK_Supplier_RiskRating CHECK (RiskRating IN (N'Low', N'Medium', N'High')),
    RiskReason          NVARCHAR(500) NULL,
    NextReviewDue       DATE NULL;
GO

CREATE TABLE contracts.SupplierContact (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierContact PRIMARY KEY,
    SupplierId  INT NOT NULL CONSTRAINT FK_SupplierContact_Supplier REFERENCES contracts.Supplier(Id),
    Name        NVARCHAR(200) NOT NULL,
    Role        NVARCHAR(200) NULL,
    Email       NVARCHAR(254) NULL,
    Phone       NVARCHAR(50) NULL,
    IsRemoved   BIT NOT NULL CONSTRAINT DF_SupplierContact_IsRemoved DEFAULT 0,   -- hidden, kept
    CreatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierContact_Created DEFAULT SYSUTCDATETIME(),
    CreatedById INT NOT NULL CONSTRAINT FK_SupplierContact_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierContact_Updated DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_SupplierContact_Supplier ON contracts.SupplierContact(SupplierId);
GO

CREATE TABLE contracts.BusinessReview (
    Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusinessReview PRIMARY KEY,
    SupplierId        INT NOT NULL CONSTRAINT FK_BusinessReview_Supplier REFERENCES contracts.Supplier(Id),
    HeldOn            DATE NOT NULL,
    Summary           NVARCHAR(4000) NOT NULL,
    SupplierAttendees NVARCHAR(1000) NULL,          -- the supplier's people, as typed
    Rating            NVARCHAR(20) NULL CONSTRAINT CK_BusinessReview_Rating CHECK (Rating IN (N'Good', N'NeedsImprovement', N'Poor')),
    QualityScore      TINYINT NULL CONSTRAINT CK_BusinessReview_Quality CHECK (QualityScore BETWEEN 1 AND 5),
    DeliveryScore     TINYINT NULL CONSTRAINT CK_BusinessReview_Delivery CHECK (DeliveryScore BETWEEN 1 AND 5),
    ServiceScore      TINYINT NULL CONSTRAINT CK_BusinessReview_Service CHECK (ServiceScore BETWEEN 1 AND 5),
    CostScore         TINYINT NULL CONSTRAINT CK_BusinessReview_Cost CHECK (CostScore BETWEEN 1 AND 5),
    IsRemoved         BIT NOT NULL CONSTRAINT DF_BusinessReview_IsRemoved DEFAULT 0,
    CreatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_BusinessReview_Created DEFAULT SYSUTCDATETIME(),
    CreatedById       INT NOT NULL CONSTRAINT FK_BusinessReview_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_BusinessReview_Updated DEFAULT SYSUTCDATETIME(),
    RowVersion        ROWVERSION NOT NULL
);
CREATE INDEX IX_BusinessReview_Supplier ON contracts.BusinessReview(SupplierId, HeldOn);
GO
-- JBS people who attended.
CREATE TABLE contracts.BusinessReviewAttendee (
    ReviewId INT NOT NULL CONSTRAINT FK_BusinessReviewAttendee_Review REFERENCES contracts.BusinessReview(Id),
    PersonId INT NOT NULL CONSTRAINT FK_BusinessReviewAttendee_Person REFERENCES contracts.Person(Id),
    CONSTRAINT PK_BusinessReviewAttendee PRIMARY KEY (ReviewId, PersonId)
);
-- Actions agreed at a review: a JBS owner and a due date; ticked done (never deleted).
CREATE TABLE contracts.BusinessReviewAction (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusinessReviewAction PRIMARY KEY,
    ReviewId    INT NOT NULL CONSTRAINT FK_BusinessReviewAction_Review REFERENCES contracts.BusinessReview(Id),
    Text        NVARCHAR(500) NOT NULL,
    OwnerId     INT NOT NULL CONSTRAINT FK_BusinessReviewAction_Owner REFERENCES contracts.Person(Id),
    DueOn       DATE NOT NULL,
    DoneUtc     DATETIME2(0) NULL,
    DoneById    INT NULL CONSTRAINT FK_BusinessReviewAction_DoneBy REFERENCES contracts.Person(Id),
    CreatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_BusinessReviewAction_Created DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_BusinessReviewAction_Owner ON contracts.BusinessReviewAction(OwnerId) WHERE DoneUtc IS NULL;
GO
-- A review's deck or minutes is a supplier document linked to the review.
ALTER TABLE contracts.SupplierDocument ADD ReviewId INT NULL CONSTRAINT FK_SupplierDocument_Review REFERENCES contracts.BusinessReview(Id);
GO

-- Reminders to the relationship owner before the next business review is due (days and wording editable).
CREATE TABLE contracts.SupplierReviewReminder (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierReviewReminder PRIMARY KEY,
    SupplierId INT NOT NULL CONSTRAINT FK_SupplierReviewReminder_Supplier REFERENCES contracts.Supplier(Id),
    PersonId   INT NOT NULL CONSTRAINT FK_SupplierReviewReminder_Person REFERENCES contracts.Person(Id),
    DueOn      DATE NOT NULL,
    DaysBefore INT NOT NULL,
    SentUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_SupplierReviewReminder_Sent DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UX_SupplierReviewReminder UNIQUE (SupplierId, PersonId, DueOn, DaysBefore)
);
GO
ALTER TABLE contracts.ReminderSetting DROP CONSTRAINT CK_ReminderSetting_Kind;
GO
ALTER TABLE contracts.ReminderSetting ADD CONSTRAINT CK_ReminderSetting_Kind
    CHECK (Kind IN (N'Notice', N'Escalation', N'End', N'Review', N'SupplierExpiry', N'SupplierReview'));
GO
INSERT INTO contracts.ReminderSetting (Kind, Days) VALUES (N'SupplierReview', N'30,7');
GO
ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack',
    N'HandedOver', N'VariationCleared', N'VariationWaiting', N'VariationApproved', N'VariationSentBack',
    N'SupplierExpiry', N'SupplierReview', N'ReviewAction'));
GO
DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'SupplierReview', N'{Supplier}: business review due in {Days} days',
     N'Hi {First name},' + @p +
     N'The next business review with {Supplier} is due on {Due date}, in {Days} days. You''re its relationship owner.' + @p +
     N'Once it''s held, add it on the supplier''s page with its actions, and set when the next one is due.'),
    (N'ReviewAction', N'{Supplier}: an action from the business review is yours',
     N'Hi {First name},' + @p +
     N'At the business review with {Supplier} on {Review date}, this action was given to you, due {Due date}:' + @p +
     N'"{Action}"' + @p +
     N'Tick it done on the supplier''s page when it''s finished.');
GO
