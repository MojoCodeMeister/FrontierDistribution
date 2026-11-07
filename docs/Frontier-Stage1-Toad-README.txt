Frontier Stage 1 ERD — Toad Data Modeler 2.25 Free
=================================================

File: Frontier-Stage1-ERD-Toad-2.25.sql
Source: FrontierDistribution commit 118607d08041, verified Stage 1 model.

Contains eight Stage 1 entities and their foreign-key relationships.
This is a SQL Server-compatible model import file, not a native .dm2.
No database changes have been made. DO NOT run this against Frontier systems.

TOAD IMPORT TEST
----------------
1. In Toad Data Modeler, look for File > Reverse Engineering or a command
   to load a model from SQL/DDL script (check the built-in Help for exact menu).
2. Select an SQL Server dialect supported by your installed version.
3. Select Frontier-Stage1-ERD-Toad-2.25.sql as the DDL source.
4. Confirm that eight entities and seven foreign-key relationships appear.
5. Save the resulting editable model as a native .dm2 file.

IMPORTANT: Toad Data Modeler 2.25 may NOT support SQL-file reverse engineering.
Quest documents the feature in later versions, but compatibility with 2.25
has not been verified. If the command is missing, do not execute the SQL
against any existing database just to get a diagram; report the available
menu options so the correct 2.25 route can be identified.

MODEL LIMITATIONS
-----------------
* The SQL representation is an approximate physical projection of a LOGICAL
  model, created solely for visual import.
* SupplierProductRowId and ProductIdentityRowId are proposed technical IDs
  for the import and are not approved domain-model attributes.
* Product identity scoped uniqueness is validated in the C# model; it is not
  represented as an SQL UNIQUE constraint here because NULL context and scope
  semantics require a separate physical design decision.
* SupplierProduct uniqueness is deliberately unresolved.
* InventoryPosition carries no actual inventory quantities or reservation
  authority (Q24 remains unresolved).
* All actual Frontier systems, schemas, ownership and database configurations
  remain unverified.
* Stage 2.1 Sales Order design is not included: the actual candidate design
  document is needed before generating a faithful second model.
