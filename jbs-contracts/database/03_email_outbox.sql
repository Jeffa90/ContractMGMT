-- JBS Contracts: emails waiting to be sent (decision D-45). Written in the same transaction as the change that causes them,
-- then sent by the app's background sender through the JBS relay, with retries. Never edit after it has run.
CREATE TABLE contracts.EmailOutbox (
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmailOutbox PRIMARY KEY,
    Kind         NVARCHAR(50)   NOT NULL,     -- e.g. access-granted
    ToAddress    NVARCHAR(320)  NOT NULL,
    ToName       NVARCHAR(200)  NOT NULL,
    Subject      NVARCHAR(300)  NOT NULL,
    BodyText     NVARCHAR(MAX)  NOT NULL,
    BodyHtml     NVARCHAR(MAX)  NOT NULL,
    PersonId     INT            NULL CONSTRAINT FK_EmailOutbox_Person REFERENCES contracts.Person(Id),
    ContractId   INT            NULL CONSTRAINT FK_EmailOutbox_Contract REFERENCES contracts.Contract(Id),
    DedupeKey    NVARCHAR(200)  NULL,         -- one email per key, so a retry never sends twice
    CreatedUtc   DATETIME2(0)   NOT NULL CONSTRAINT DF_EmailOutbox_CreatedUtc DEFAULT SYSUTCDATETIME(),
    NextTryUtc   DATETIME2(3)   NOT NULL CONSTRAINT DF_EmailOutbox_NextTryUtc DEFAULT SYSUTCDATETIME(),  -- milliseconds, so a new email is due at once
    Attempts     INT            NOT NULL CONSTRAINT DF_EmailOutbox_Attempts DEFAULT 0,
    SentUtc      DATETIME2(0)   NULL,
    FailedUtc    DATETIME2(0)   NULL,         -- gave up after the last attempt
    LastError    NVARCHAR(1000) NULL
);
CREATE UNIQUE INDEX UX_EmailOutbox_Dedupe ON contracts.EmailOutbox(DedupeKey) WHERE DedupeKey IS NOT NULL;
CREATE INDEX IX_EmailOutbox_Pending ON contracts.EmailOutbox(NextTryUtc) WHERE SentUtc IS NULL AND FailedUtc IS NULL;
GO
