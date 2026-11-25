-- Every email's wording is editable by admins (Jeff, 25 Nov; D-73), not only the reminders. Seeded with the wording used until now.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.EmailTemplate DROP CONSTRAINT CK_EmailTemplate_Kind;
GO
ALTER TABLE contracts.EmailTemplate ADD CONSTRAINT CK_EmailTemplate_Kind CHECK (Kind IN (
    N'Notice', N'Escalation', N'End', N'Review',
    N'AccessGranted', N'LegalNew', N'LegalReturned', N'LegalAssigned', N'BackFromLegal', N'LegalCleared',
    N'ApprovalWaiting', N'Approved', N'ApprovalSentBack'));
GO

DECLARE @p NVARCHAR(2) = CHAR(10) + CHAR(10);   -- a blank line between paragraphs
INSERT INTO contracts.EmailTemplate (Kind, Subject, Body) VALUES
    (N'AccessGranted', N'You''ve been given access to JBS Contracts',
     N'Hi {First name},' + @p +
     N'{Added by} has added you to JBS Contracts, where JBS raises, reviews, approves and keeps track of supplier contracts.' + @p +
     N'What you can do: {Roles}.' + @p +
     N'You sign in automatically with your Windows account ({Windows logon}). There''s no password to remember.'),
    (N'LegalNew', N'{Reference} {Contract title} is with Legal',
     N'Hi {First name},' + @p +
     N'{Sent by} has sent {Reference} {Contract title} to Legal. Nobody in Legal has picked it up yet: open it and choose I''ll take it.' + @p +
     N'{Sent by} wrote: "{Note}"'),
    (N'LegalReturned', N'{Reference} {Contract title} is back with Legal',
     N'Hi {First name},' + @p +
     N'{Sent by} has sent {Reference} {Contract title} back to you. It''s Legal''s turn.' + @p +
     N'{Sent by} wrote: "{Note}"'),
    (N'LegalAssigned', N'{Reference} {Contract title}: the Legal review is yours',
     N'Hi {First name},' + @p +
     N'{Assigned by} has given you the Legal review of {Reference} {Contract title}. Open it to see where it''s up to and whose turn it is.'),
    (N'BackFromLegal', N'{Reference} {Contract title} is back with you from Legal',
     N'Hi {First name},' + @p +
     N'{Legal person} (Legal) has sent {Reference} {Contract title} back to the owner. It''s your turn.' + @p +
     N'{Legal person} wrote: "{Note}"' + @p +
     N'When you''re ready, open the contract, make any changes and choose Send to Legal.'),
    (N'LegalCleared', N'{Reference} {Contract title} has been cleared by Legal',
     N'Hi {First name},' + @p +
     N'{Legal person} (Legal) has cleared {Reference} {Contract title}. It has moved to Signing.' + @p +
     N'Outcome: "{Outcome}"' + @p +
     N'Next: sign it in DocuSign, then choose Send for approval on the contract with the signed copy and the effective date.'),
    (N'ApprovalWaiting', N'{Reference} {Contract title} is waiting for your approval',
     N'Hi {First name},' + @p +
     N'{Sent by} has sent {Reference} {Contract title} with {Supplier} for approval, effective {Effective date}. It has been signed in DocuSign and the signed copy is attached to the contract.' + @p +
     N'Check the signed copy and details. Approve and register makes it Effective; if something isn''t right, send it back with a reason.'),
    (N'Approved', N'{Reference} {Contract title} is approved and Effective',
     N'Hi {First name},' + @p +
     N'{Approver} has approved and registered {Reference} {Contract title}. It is now Effective and locked.'),
    (N'ApprovalSentBack', N'{Reference} {Contract title} was sent back from approval',
     N'Hi {First name},' + @p +
     N'{Approver} sent {Reference} {Contract title} back to Signing.' + @p +
     N'{Approver} wrote: "{Reason}"' + @p +
     N'Fix what''s needed (for example, add the right signed copy), then choose Send for approval again.');
GO
