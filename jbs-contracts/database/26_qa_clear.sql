-- Clear contracts, suppliers and lists on QA before a rehearsal import (Jeff, 26 Nov, D-111). The Legal conversation and approval
-- decisions are append-only; their triggers now also allow a delete, but only inside the QA clear, which marks its own database
-- session (SESSION_CONTEXT jbs.qa-clear). The app only does that on QA (never in Production). The audit trail is never cleared.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TRIGGER contracts.TR_LegalTurn_AppendOnly ON contracts.LegalTurn
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N'jbs.qa-clear') AS INT) = 1 AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DELETE t FROM contracts.LegalTurn t JOIN deleted d ON d.Id = t.Id;
        RETURN;
    END;
    THROW 51000, 'Legal conversation entries cannot be changed or deleted.', 1;
END;
GO
ALTER TRIGGER contracts.TR_LegalTurnDocument_AppendOnly ON contracts.LegalTurnDocument
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N'jbs.qa-clear') AS INT) = 1 AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DELETE t FROM contracts.LegalTurnDocument t JOIN deleted d ON d.LegalTurnId = t.LegalTurnId AND d.DocumentVersionId = t.DocumentVersionId;
        RETURN;
    END;
    THROW 51000, 'Legal conversation entries cannot be changed or deleted.', 1;
END;
GO
ALTER TRIGGER contracts.TR_Approval_AppendOnly ON contracts.Approval
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N'jbs.qa-clear') AS INT) = 1 AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DELETE t FROM contracts.Approval t JOIN deleted d ON d.Id = t.Id;
        RETURN;
    END;
    THROW 51000, 'Approval decisions cannot be changed or deleted.', 1;
END;
GO
