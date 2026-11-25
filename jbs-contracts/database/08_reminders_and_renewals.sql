-- Slice 6: reminders, renewal decisions, renewing, ending and archiving (product-spec sections 8 and 9; D-12 to D-14, D-32, D-67 to D-72).
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Contract ADD
    RenewalDecision      NVARCHAR(20)   NULL CONSTRAINT CK_Contract_RenewalDecision CHECK (RenewalDecision IN (N'Renew', N'LetItEnd', N'Renegotiate')),
    RenewalDecisionUtc   DATETIME2(0)   NULL,
    RenewalDecisionById  INT            NULL CONSTRAINT FK_Contract_RenewalDecisionBy REFERENCES contracts.Person(Id),
    RenewalDecisionNote  NVARCHAR(1000) NULL,
    PlannedEndDate       DATE           NULL,   -- ends on this date: Let it end on an evergreen contract, or End early with a future date
    EndedOn              DATE           NULL,
    EndedReason          NVARCHAR(1000) NULL,
    EndedById            INT            NULL CONSTRAINT FK_Contract_EndedBy REFERENCES contracts.Person(Id),
    RenewsContractId     INT            NULL CONSTRAINT FK_Contract_Renews REFERENCES contracts.Contract(Id),
    LastReviewedOn       DATE           NULL,
    LastReviewedById     INT            NULL CONSTRAINT FK_Contract_LastReviewedBy REFERENCES contracts.Person(Id);
GO
CREATE INDEX IX_Contract_Renews ON contracts.Contract(RenewsContractId) WHERE RenewsContractId IS NOT NULL;
GO

-- Every reminder sent: one row per contract, kind, date it is about and days before. The unique key means a reminder is never sent twice.
CREATE TABLE contracts.Reminder (
    Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Reminder PRIMARY KEY,
    ContractId  INT NOT NULL CONSTRAINT FK_Reminder_Contract REFERENCES contracts.Contract(Id),
    Kind        NVARCHAR(20) NOT NULL CONSTRAINT CK_Reminder_Kind CHECK (Kind IN (N'Notice', N'Escalation', N'End', N'Review')),
    DueDate     DATE NOT NULL,          -- the date it's about: notice deadline, end date or review date
    DaysBefore  INT NOT NULL,
    SentUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_Reminder_SentUtc DEFAULT SYSUTCDATETIME(),
    Recipients  NVARCHAR(1000) NOT NULL,
    CONSTRAINT UX_Reminder UNIQUE (ContractId, Kind, DueDate, DaysBefore)
);
GO

-- When the daily job last ran, so it runs once a day and catches up after a missed day.
CREATE TABLE contracts.JobRun (
    Name        NVARCHAR(50) NOT NULL CONSTRAINT PK_JobRun PRIMARY KEY,
    LastRunDate DATE NOT NULL,
    LastRunUtc  DATETIME2(0) NOT NULL
);
GO

-- Reminder days (admins change them; history kept): whole days before, largest first.
CREATE TABLE contracts.ReminderSetting (
    Kind       NVARCHAR(20) NOT NULL CONSTRAINT PK_ReminderSetting PRIMARY KEY
        CONSTRAINT CK_ReminderSetting_Kind CHECK (Kind IN (N'Notice', N'Escalation', N'End', N'Review')),
    Days       NVARCHAR(100) NOT NULL,   -- days before, comma separated
    UpdatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_ReminderSetting_Updated DEFAULT SYSUTCDATETIME(),
    UpdatedById INT NULL CONSTRAINT FK_ReminderSetting_By REFERENCES contracts.Person(Id)
);
INSERT INTO contracts.ReminderSetting (Kind, Days) VALUES
    (N'Notice', N'90,60,30,14,7'), (N'Escalation', N'7'), (N'End', N'180,90,30'), (N'Review', N'90,60,30');
GO

-- Reminder email wording (admins change it; history kept). Plain text with a fixed list of {placeholders}; never HTML or code.
CREATE TABLE contracts.EmailTemplate (
    Kind        NVARCHAR(20) NOT NULL CONSTRAINT PK_EmailTemplate PRIMARY KEY
        CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (N'Notice', N'Escalation', N'End', N'Review')),
    Subject     NVARCHAR(200) NOT NULL,
    Body        NVARCHAR(4000) NOT NULL,
    UpdatedUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_EmailTemplate_Updated DEFAULT SYSUTCDATETIME(),
    UpdatedById INT NULL CONSTRAINT FK_EmailTemplate_By REFERENCES contracts.Person(Id)
);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'Notice', N'{Reference} {Contract title}: notice deadline in {Days} days',
     N'Hi {First name},' + CHAR(10) + CHAR(10) +
     N'The last day to give notice on {Reference} {Contract title} with {Supplier} is {Due date}, in {Days} days.' + CHAR(10) + CHAR(10) +
     N'Decide whether to renew it, let it end or renegotiate, and record your decision on the contract. Once you do, these reminders stop.'),
    (N'Escalation', N'{Reference} {Contract title}: notice deadline in {Days} days and no decision yet',
     N'Hi {First name},' + CHAR(10) + CHAR(10) +
     N'The notice deadline for {Reference} {Contract title} with {Supplier} is {Due date}, in {Days} days, and no renewal decision has been recorded. The owner is {Owner}.' + CHAR(10) + CHAR(10) +
     N'Please make sure a decision is made and recorded on the contract before the deadline.'),
    (N'End', N'{Reference} {Contract title} ends in {Days} days',
     N'Hi {First name},' + CHAR(10) + CHAR(10) +
     N'{Reference} {Contract title} with {Supplier} ends on {Due date}, in {Days} days.' + CHAR(10) + CHAR(10) +
     N'If you haven''t already, record whether to renew it, let it end or renegotiate.'),
    (N'Review', N'{Reference} {Contract title}: review due in {Days} days',
     N'Hi {First name},' + CHAR(10) + CHAR(10) +
     N'{Reference} {Contract title} with {Supplier} is due for review on {Due date}, in {Days} days.' + CHAR(10) + CHAR(10) +
     N'When you''ve reviewed it, choose Review done on the contract and enter the next review date.');
GO

-- History of reminder days and wording: every saved change, with who and when.
CREATE TABLE contracts.ReminderChange (
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReminderChange PRIMARY KEY,
    Kind      NVARCHAR(20) NOT NULL,
    What      NVARCHAR(20) NOT NULL CONSTRAINT CK_ReminderChange_What CHECK (What IN (N'Days', N'Wording')),
    OldValue  NVARCHAR(MAX) NOT NULL,
    NewValue  NVARCHAR(MAX) NOT NULL,
    ById      INT NOT NULL CONSTRAINT FK_ReminderChange_By REFERENCES contracts.Person(Id),
    AtUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_ReminderChange_At DEFAULT SYSUTCDATETIME()
);
GO
