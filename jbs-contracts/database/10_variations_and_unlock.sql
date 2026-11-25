-- Slice 7: Variations (changing an Effective contract the proper way) and an admin's emergency unlock (product-spec section 7; D-11, D-74 to D-80).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.Variation (
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Variation PRIMARY KEY,
    ContractId       INT NOT NULL CONSTRAINT FK_Variation_Contract REFERENCES contracts.Contract(Id),
    Number           INT NOT NULL,                       -- Variation 1, 2, ... per contract
    Reason           NVARCHAR(2000) NOT NULL,
    ChangesJson      NVARCHAR(MAX) NOT NULL CONSTRAINT DF_Variation_Changes DEFAULT N'{}',   -- field key to the proposed value
    NeedsLegalReview BIT NOT NULL CONSTRAINT DF_Variation_NeedsLegal DEFAULT 1,
    Stage            NVARCHAR(20) NOT NULL CONSTRAINT DF_Variation_Stage DEFAULT N'Draft'
        CONSTRAINT CK_Variation_Stage CHECK (Stage IN (N'Draft', N'LegalReview', N'Approval', N'Approved', N'Withdrawn')),
    StageSinceUtc    DATETIME2(0) NOT NULL CONSTRAINT DF_Variation_StageSince DEFAULT SYSUTCDATETIME(),
    WhoseTurn        NVARCHAR(10) NULL CONSTRAINT CK_Variation_WhoseTurn CHECK (WhoseTurn IN (N'Legal', N'Owner')),
    LegalPersonId    INT NULL CONSTRAINT FK_Variation_LegalPerson REFERENCES contracts.Person(Id),
    TurnSinceUtc     DATETIME2(0) NULL,
    LegalOutcome     NVARCHAR(2000) NULL,
    LegalClearedUtc  DATETIME2(0) NULL,
    LegalClearedById INT NULL CONSTRAINT FK_Variation_LegalClearedBy REFERENCES contracts.Person(Id),
    ClosedReason     NVARCHAR(2000) NULL,                -- why it was withdrawn
    AppliedChangesJson NVARCHAR(MAX) NULL,               -- once approved: each field's old and new value, as applied
    CreatedUtc       DATETIME2(0) NOT NULL CONSTRAINT DF_Variation_Created DEFAULT SYSUTCDATETIME(),
    CreatedById      INT NOT NULL CONSTRAINT FK_Variation_CreatedBy REFERENCES contracts.Person(Id),
    UpdatedUtc       DATETIME2(0) NOT NULL CONSTRAINT DF_Variation_Updated DEFAULT SYSUTCDATETIME(),
    RowVersion       ROWVERSION NOT NULL,
    CONSTRAINT UX_Variation UNIQUE (ContractId, Number)
);
-- One open Variation per contract at a time.
CREATE UNIQUE INDEX UX_Variation_Open ON contracts.Variation(ContractId) WHERE Stage IN (N'Draft', N'LegalReview', N'Approval');
GO

-- Legal conversations and approval decisions can be about a Variation; documents can belong to one.
ALTER TABLE contracts.LegalTurn ADD VariationId INT NULL CONSTRAINT FK_LegalTurn_Variation REFERENCES contracts.Variation(Id);
ALTER TABLE contracts.Approval ADD VariationId INT NULL CONSTRAINT FK_Approval_Variation REFERENCES contracts.Variation(Id);
ALTER TABLE contracts.Document ADD VariationId INT NULL CONSTRAINT FK_Document_Variation REFERENCES contracts.Variation(Id);
GO
-- Entries were numbered per contract; now per contract for the contract's own review, and per Variation for each Variation.
ALTER TABLE contracts.LegalTurn DROP CONSTRAINT UX_LegalTurn;
GO
CREATE UNIQUE INDEX UX_LegalTurn_Contract ON contracts.LegalTurn(ContractId, Number) WHERE VariationId IS NULL;
CREATE UNIQUE INDEX UX_LegalTurn_Variation ON contracts.LegalTurn(VariationId, Number) WHERE VariationId IS NOT NULL;
GO

-- Emergency unlock: an admin, a reason, at most 4 hours; closed early or when it expires. Never deleted.
CREATE TABLE contracts.Unlock (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Unlock PRIMARY KEY,
    ContractId  INT NOT NULL CONSTRAINT FK_Unlock_Contract REFERENCES contracts.Contract(Id),
    ById        INT NOT NULL CONSTRAINT FK_Unlock_By REFERENCES contracts.Person(Id),
    Reason      NVARCHAR(1000) NOT NULL,
    StartedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_Unlock_Started DEFAULT SYSUTCDATETIME(),
    ExpiresUtc  DATETIME2(0) NOT NULL,
    ClosedUtc   DATETIME2(0) NULL,
    ClosedById  INT NULL CONSTRAINT FK_Unlock_ClosedBy REFERENCES contracts.Person(Id),
    CONSTRAINT CK_Unlock_AtMostFourHours CHECK (ExpiresUtc > StartedUtc AND ExpiresUtc <= DATEADD(HOUR, 4, StartedUtc))
);
CREATE INDEX IX_Unlock_Contract ON contracts.Unlock(ContractId);
GO

-- Emails for Variations and hand-overs (wording editable, D-73).
ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack',
    N'HandedOver', N'VariationCleared', N'VariationWaiting', N'VariationApproved', N'VariationSentBack'));
GO
DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'HandedOver', N'{Reference} {Contract title} is now yours',
     N'Hi {First name},' + @p +
     N'{Handed over by} has made you the {Role} of {Reference} {Contract title} with {Supplier}.' + @p +
     N'You''ll get its reminders and emails from now on.'),
    (N'VariationCleared', N'{Reference} {Contract title}: {Variation} has been cleared by Legal',
     N'Hi {First name},' + @p +
     N'{Legal person} (Legal) has cleared {Variation} of {Reference} {Contract title}.' + @p +
     N'Outcome: "{Outcome}"' + @p +
     N'Next: attach the signed variation document if there is one, then send it for approval.'),
    (N'VariationWaiting', N'{Reference} {Contract title}: {Variation} is waiting for your approval',
     N'Hi {First name},' + @p +
     N'{Sent by} has sent {Variation} of {Reference} {Contract title} for approval.' + @p +
     N'Why: "{Reason}"' + @p +
     N'Changes: {Changes}.' + @p +
     N'Approve it to apply the changes, or send it back with a reason.'),
    (N'VariationApproved', N'{Reference} {Contract title}: {Variation} is approved',
     N'Hi {First name},' + @p +
     N'{Approver} has approved {Variation} of {Reference} {Contract title}. The changes now apply: {Changes}.'),
    (N'VariationSentBack', N'{Reference} {Contract title}: {Variation} was sent back',
     N'Hi {First name},' + @p +
     N'{Approver} sent {Variation} of {Reference} {Contract title} back.' + @p +
     N'{Approver} wrote: "{Reason}"' + @p +
     N'Change what''s needed, then send it for approval again.');
GO
