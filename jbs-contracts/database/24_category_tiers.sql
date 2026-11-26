-- Supplier tiers from spend categories (Jeff, 26 Nov, D-108). Each category can have a supplier tier (a subcategory without one
-- follows its category). A supplier's tier is then, unless Procurement sets one by hand, the highest tier among the categories
-- of its current contracts: highest is the one nearer the top of the Supplier tiers list. Supplier.TierId stays the tier set by
-- hand; null now means "worked out from its contracts".
-- Run once by Invoke-SqlMigrations.ps1. Never edit after it has been applied; add a new numbered script instead.

ALTER TABLE contracts.Category ADD TierId INT NULL CONSTRAINT FK_Category_Tier REFERENCES contracts.ListItem(Id);
GO
