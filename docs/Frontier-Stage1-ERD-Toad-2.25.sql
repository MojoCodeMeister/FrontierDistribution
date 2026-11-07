/*
 Frontier — Stage 1 candidate logical ERD rendered as SQL Server DDL
 Source: FrontierDistribution commit 118607d08041, src/Frontier.Domain/Entities.cs,
         docs/logical-erd.md; reviewed Stage 1 corrections (20/20 tests).
 Purpose: Create an EDITABLE ERD through Toad Data Modeler reverse engineering.
 NOT FRONTIER'S REAL DATABASE SCHEMA. DO NOT EXECUTE AGAINST PRODUCTION.
 This DDL is a visualization/import INTERCHANGE, not approved physical design.
 Tables and SQL data types are provisional projections of the C# records.
 Hypothetical surrogate keys are noted below. Q24 is not resolved.
 SQL Server 2005-compatible syntax for legacy modeling-tool compatibility.
*/

CREATE TABLE dbo.Supplier (
    SupplierId varchar(64) NOT NULL,
    Name varchar(200) NOT NULL,
    CONSTRAINT PK_Supplier PRIMARY KEY (SupplierId)
);

CREATE TABLE dbo.Product (
    ProductId varchar(64) NOT NULL,
    Description varchar(500) NOT NULL,
    BaseUnit varchar(32) NOT NULL,
    CONSTRAINT PK_Product PRIMARY KEY (ProductId)
);

CREATE TABLE dbo.Customer (
    CustomerId varchar(64) NOT NULL,
    Name varchar(200) NOT NULL,
    CONSTRAINT PK_Customer PRIMARY KEY (CustomerId)
);

CREATE TABLE dbo.Warehouse (
    WarehouseId varchar(64) NOT NULL,
    Name varchar(200) NOT NULL,
    CONSTRAINT PK_Warehouse PRIMARY KEY (WarehouseId)
);

CREATE TABLE dbo.CustomerLocation (
    LocationId varchar(64) NOT NULL,
    CustomerId varchar(64) NOT NULL,
    Label varchar(200) NOT NULL,
    CONSTRAINT PK_CustomerLocation PRIMARY KEY (LocationId),
    CONSTRAINT FK_CustomerLocation_Customer FOREIGN KEY (CustomerId)
        REFERENCES dbo.Customer(CustomerId)
);

/* SupplierProduct key is UNRESOLVED in the logical design.
   Surrogate key below is a modeling placeholder, NOT an accepted business rule.
   No uniqueness constraint is imposed on (SupplierId,ProductId). */
CREATE TABLE dbo.SupplierProduct (
    SupplierProductRowId int IDENTITY(1,1) NOT NULL,
    SupplierId varchar(64) NOT NULL,
    ProductId varchar(64) NOT NULL,
    SupplierItemNumber varchar(100) NOT NULL,
    PurchaseUnit varchar(32) NOT NULL,
    BaseUnitsPerPurchaseUnit decimal(18,6) NOT NULL,
    CONSTRAINT PK_SupplierProduct PRIMARY KEY (SupplierProductRowId),
    CONSTRAINT FK_SupplierProduct_Supplier FOREIGN KEY (SupplierId)
        REFERENCES dbo.Supplier(SupplierId),
    CONSTRAINT FK_SupplierProduct_Product FOREIGN KEY (ProductId)
        REFERENCES dbo.Product(ProductId)
);

/* ProductIdentity's surrogate row key exists only to make this candidate
   SQL model importable. Identity uniqueness derives from
   System + IdentifierType + ExternalId + Context, with NULL context semantics
   and scope validation to be addressed before physical implementation.
   No potentially incorrect UNIQUE constraint is asserted here. */
CREATE TABLE dbo.ProductIdentity (
    ProductIdentityRowId int IDENTITY(1,1) NOT NULL,
    ProductId varchar(64) NOT NULL,
    [System] varchar(80) NOT NULL,
    IdentifierType varchar(80) NOT NULL,
    ExternalId varchar(150) NOT NULL,
    [Context] varchar(150) NULL,
    CONSTRAINT PK_ProductIdentity PRIMARY KEY (ProductIdentityRowId),
    CONSTRAINT FK_ProductIdentity_Product FOREIGN KEY (ProductId)
        REFERENCES dbo.Product(ProductId)
);

/* InventoryPosition is an association only: NO quantities, reservations,
   allocations, ownership, or Q24 inventory-authority decision. Candidate
   composite key means one relationship per warehouse/product pair. */
CREATE TABLE dbo.InventoryPosition (
    WarehouseId varchar(64) NOT NULL,
    ProductId varchar(64) NOT NULL,
    CONSTRAINT PK_InventoryPosition PRIMARY KEY (WarehouseId, ProductId),
    CONSTRAINT FK_InventoryPosition_Warehouse FOREIGN KEY (WarehouseId)
        REFERENCES dbo.Warehouse(WarehouseId),
    CONSTRAINT FK_InventoryPosition_Product FOREIGN KEY (ProductId)
        REFERENCES dbo.Product(ProductId)
);
