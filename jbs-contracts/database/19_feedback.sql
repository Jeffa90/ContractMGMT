-- Feedback (D-101): a Feedback button on every screen and a Feedback page. Everyone sees and replies to their own; admins see all,
-- reply, set the status and archive, and are emailed when new feedback comes in. Kept in this database for now; it can move to the
-- shared JBSAppsCommon store later (docs/features/feedback.md). Nothing is deleted: items are archived.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.Feedback (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Feedback PRIMARY KEY,
    Kind        NVARCHAR(20) NOT NULL CONSTRAINT CK_Feedback_Kind CHECK (Kind IN (N'Bug', N'Improvement', N'DataWrong', N'Question', N'Other')),
    Comment     NVARCHAR(4000) NOT NULL,
    Screen      NVARCHAR(500) NULL,                 -- where it was sent from, e.g. "Contracts (/contracts)"
    Status      NVARCHAR(20) NOT NULL CONSTRAINT DF_Feedback_Status DEFAULT N'Open'
        CONSTRAINT CK_Feedback_Status CHECK (Status IN (N'Open', N'InProgress', N'Done', N'WontDo')),
    IsArchived  BIT NOT NULL CONSTRAINT DF_Feedback_IsArchived DEFAULT 0,
    CreatedById INT NOT NULL CONSTRAINT FK_Feedback_CreatedBy REFERENCES contracts.Person(Id),
    CreatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_Feedback_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_Feedback_Updated DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Feedback_CreatedBy ON contracts.Feedback(CreatedById);
GO
CREATE TABLE contracts.FeedbackReply (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FeedbackReply PRIMARY KEY,
    FeedbackId INT NOT NULL CONSTRAINT FK_FeedbackReply_Feedback REFERENCES contracts.Feedback(Id),
    ById       INT NOT NULL CONSTRAINT FK_FeedbackReply_By REFERENCES contracts.Person(Id),
    Text       NVARCHAR(4000) NOT NULL,
    CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_FeedbackReply_Created DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_FeedbackReply_Feedback ON contracts.FeedbackReply(FeedbackId);
GO

-- The email to admins when new feedback comes in (wording editable like every other email).
ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack',
    N'HandedOver', N'VariationCleared', N'VariationWaiting', N'VariationApproved', N'VariationSentBack',
    N'SupplierExpiry', N'SupplierReview', N'ReviewAction', N'FeedbackNew'));
GO
DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'FeedbackNew', N'New feedback from {Sent by}: {Feedback type}',
     N'Hi {First name},' + @p +
     N'{Sent by} sent feedback ({Feedback type}) from {Screen}:' + @p +
     N'"{Comment}"' + @p +
     N'Reply to them and set its status on the Feedback page.');
GO
