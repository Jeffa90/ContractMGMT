-- The spend taxonomy (Jeff, 26 Nov, D-115): categories go three levels deep, Family > Category > Subcategory (the Category
-- table's ParentId already allows it; the app keeps it to three). A contract keeps one main category (its main spend: it sets
-- the supplier tier and is where the value counts) and can have other categories, kept here.
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

CREATE TABLE contracts.ContractCategory (
    ContractId INT NOT NULL CONSTRAINT FK_ContractCategory_Contract REFERENCES contracts.Contract(Id),
    CategoryId INT NOT NULL CONSTRAINT FK_ContractCategory_Category REFERENCES contracts.Category(Id),
    CONSTRAINT PK_ContractCategory PRIMARY KEY (ContractId, CategoryId)
);
CREATE INDEX IX_ContractCategory_Category ON contracts.ContractCategory(CategoryId);
GO
