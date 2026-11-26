-- Business reviews (Declan's feedback, D-104): the supplier's people at a review are picked from the supplier's contacts, and an
-- action can be for the supplier (optionally one of its contacts) instead of a JBS person. Supplier actions aren't emailed to
-- the supplier; the relationship owner sees them on Home and follows them up. Older reviews keep their typed supplier attendees.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.BusinessReviewContact (
    ReviewId  INT NOT NULL CONSTRAINT FK_BusinessReviewContact_Review REFERENCES contracts.BusinessReview(Id),
    ContactId INT NOT NULL CONSTRAINT FK_BusinessReviewContact_Contact REFERENCES contracts.SupplierContact(Id),
    CONSTRAINT PK_BusinessReviewContact PRIMARY KEY (ReviewId, ContactId)
);
GO

-- OwnerId null: the action is for the supplier (SupplierContactId says who there, if anyone).
DROP INDEX IX_BusinessReviewAction_Owner ON contracts.BusinessReviewAction;
GO
ALTER TABLE contracts.BusinessReviewAction ALTER COLUMN OwnerId INT NULL;
GO
ALTER TABLE contracts.BusinessReviewAction ADD
    SupplierContactId INT NULL CONSTRAINT FK_BusinessReviewAction_Contact REFERENCES contracts.SupplierContact(Id),
    CONSTRAINT CK_BusinessReviewAction_OneOwner CHECK (OwnerId IS NULL OR SupplierContactId IS NULL);
GO
CREATE INDEX IX_BusinessReviewAction_Owner ON contracts.BusinessReviewAction(OwnerId) WHERE DoneUtc IS NULL;
GO
