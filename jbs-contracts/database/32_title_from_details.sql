-- D-132: whether a contract's title is always made from its details (Supplier_Agreement type_What it covers) or people can type
-- their own. Made from the details to begin with (Jeff); an admin switches it under Admin, Contract fields.
IF NOT EXISTS (SELECT 1 FROM contracts.AppSetting WHERE Name = N'TitleFromDetails')
    INSERT INTO contracts.AppSetting (Name, Value) VALUES (N'TitleFromDetails', N'Yes');
