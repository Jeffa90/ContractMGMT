-- Supplier groups (Jeff, 26 Nov, D-116), for example a franchise: each franchisee is its own supplier (its own SAP vendor number,
-- contacts and documents) in the group of a parent supplier, usually the head company that signs. Contracts sit with the parent and
-- cover the whole group. One level only: a group isn't inside another group.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Supplier ADD ParentSupplierId INT NULL CONSTRAINT FK_Supplier_Parent REFERENCES contracts.Supplier(Id);
GO
CREATE INDEX IX_Supplier_Parent ON contracts.Supplier(ParentSupplierId) WHERE ParentSupplierId IS NOT NULL;
CREATE INDEX IX_Supplier_VendorNumber ON contracts.Supplier(VendorNumber) WHERE VendorNumber IS NOT NULL;
GO
