-- JBS Contracts: the schema, created without naming an owner (the deploy account holds db_ddladmin, which can create a
-- schema but not hand it to dbo). Sorts before 01, whose own IF SCHEMA_ID check then skips. Never edit after it has run.
IF SCHEMA_ID(N'contracts') IS NULL
    EXEC(N'CREATE SCHEMA contracts');
GO
