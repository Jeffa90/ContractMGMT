-- Reminder summaries and switching a reminder off (D-99). Each person gets their reminders as they happen, in one email a day, or in
-- one email a week (Mondays, sooner when something is a week or less away); an admin sets the default and each person can change
-- their own. Admins can also switch a whole reminder off. Emails that hand over a turn (sent to Legal, waiting for approval) still go
-- at once. Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.ReminderSetting ADD IsOn BIT NOT NULL CONSTRAINT DF_ReminderSetting_IsOn DEFAULT 1;
ALTER TABLE contracts.Person ADD ReminderDelivery NVARCHAR(10) NULL
    CONSTRAINT CK_Person_ReminderDelivery CHECK (ReminderDelivery IN (N'Each', N'Daily', N'Weekly'));   -- NULL: the default
GO

CREATE TABLE contracts.AppSetting (
    Name        NVARCHAR(50)  NOT NULL CONSTRAINT PK_AppSetting PRIMARY KEY,
    Value       NVARCHAR(200) NOT NULL,
    UpdatedUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_AppSetting_Updated DEFAULT SYSUTCDATETIME(),
    UpdatedById INT NULL CONSTRAINT FK_AppSetting_By REFERENCES contracts.Person(Id)
);
INSERT INTO contracts.AppSetting (Name, Value) VALUES (N'ReminderDelivery', N'Daily');
GO

ALTER TABLE contracts.ReminderChange DROP CONSTRAINT CK_ReminderChange_What;
GO
ALTER TABLE contracts.ReminderChange ADD CONSTRAINT CK_ReminderChange_What CHECK (What IN (N'Days', N'Wording', N'OnOff', N'Delivery'));
GO

-- Reminders waiting to go in someone's next summary email.
CREATE TABLE contracts.ReminderSummaryItem (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReminderSummaryItem PRIMARY KEY,
    PersonId   INT NOT NULL CONSTRAINT FK_ReminderSummaryItem_Person REFERENCES contracts.Person(Id),
    Kind       NVARCHAR(20) NOT NULL,
    Line       NVARCHAR(1000) NOT NULL,
    Link       NVARCHAR(500) NULL,
    DueDate    DATE NOT NULL,
    DaysLeft   INT NOT NULL,
    ContractId INT NULL CONSTRAINT FK_ReminderSummaryItem_Contract REFERENCES contracts.Contract(Id),
    SupplierId INT NULL CONSTRAINT FK_ReminderSummaryItem_Supplier REFERENCES contracts.Supplier(Id),
    CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_ReminderSummaryItem_Created DEFAULT SYSUTCDATETIME(),
    SentUtc    DATETIME2(0) NULL
);
CREATE INDEX IX_ReminderSummaryItem_Waiting ON contracts.ReminderSummaryItem(PersonId) WHERE SentUtc IS NULL;
GO
