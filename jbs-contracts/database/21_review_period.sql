-- The review date can be a period before the end date (Declan's feedback, D-105): 1, 3, 6 or 12 months. The review date is still
-- stored (reminders, reports and Home use it) and is worked out again whenever the end date moves: an edit, a Variation or an
-- automatic renewal. Null: a date chosen by hand, or none.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Contract ADD ReviewMonthsBeforeEnd INT NULL
    CONSTRAINT CK_Contract_ReviewMonthsBeforeEnd CHECK (ReviewMonthsBeforeEnd BETWEEN 1 AND 24);
GO
UPDATE contracts.FieldDefinition SET Help = N'When to review it: a period before the end date (it moves with the end date), or a date.'
WHERE [Key] = N'reviewDate' AND Help IS NULL;
GO
