-- Undo an approval (Ellisha's feedback, D-106): the approver who approved a contract can undo it within 24 hours, as long as nothing
-- else has happened to it since. It goes back to waiting for approval. The approval stays in the record, followed by Undone.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Approval DROP CONSTRAINT CK_Approval_Decision;
GO
ALTER TABLE contracts.Approval ADD CONSTRAINT CK_Approval_Decision CHECK (Decision IN (N'Approved', N'SentBack', N'Undone'));
GO

ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack', N'ApprovalUndone',
    N'HandedOver', N'VariationCleared', N'VariationWaiting', N'VariationApproved', N'VariationSentBack',
    N'SupplierExpiry', N'SupplierReview', N'ReviewAction', N'FeedbackNew'));
GO
DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'ApprovalUndone', N'{Reference} {Contract title}: approval undone',
     N'Hi {First name},' + @p +
     N'{Approver} undid their approval of {Reference} {Contract title}, so it isn''t Effective yet. It''s waiting for approval again.' + @p +
     N'Nothing is needed from you unless an approver sends it back.');
GO
