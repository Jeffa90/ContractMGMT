-- D-128: new contract numbers start at JBSAUS0001001, clear of Medius's numbers (JBSAUS0000001 to JBSAUS0000616 and
-- whatever Medius adds before it's switched off), so a contract raised before the Medius import never takes a Medius number.
-- Numbers already given keep their number; an import still carries the counter on past any higher number.
UPDATE contracts.Counter SET NextValue = 1001 WHERE Name = N'ContractNumber' AND NextValue < 1001;
