/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 020: ERP master data

    Source records: src/Frontier.Domain/Entities.cs
    Rules:          src/Frontier.Identity/IdentityCatalog.cs (Validate)
    Owner:          ERP (decision D2, approved 2026-11-11)

    Identifier columns use Latin1_General_100_CI_AS explicitly, so keys compare
    case-insensitively (decision D9) whatever the database default collation is.
    Descriptive text uses the database default.

    The database enforces single-table and same-model rules only. The C# catalog
    validation remains authoritative; NotBlank checks reject empty and space-only
    values, and other whitespace stays a service rule.
    Synthetic model. Not Frontier's production database. Rerunnable.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'erp') IS NULL
    THROW 50001, N'Schema erp is missing. Run 010-erp-schema.sql first.', 1;

/* Customer ---------------------------------------------------------------- */
IF OBJECT_ID(N'erp.Customer', N'U') IS NULL
CREATE TABLE erp.Customer
(
    CustomerId  varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    Name        nvarchar(200) NOT NULL,
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId),
    CONSTRAINT CK_Customer_CustomerId_NotBlank CHECK (LEN(CustomerId) > 0)
);

/* CustomerLocation: one customer has many locations; LocationId is unique
   across all customers. UQ_CustomerLocation_Location_Customer adds no rule
   (it contains the PK); it is the target of the sales-order ship-to foreign key
   in increment 2, which enforces "ship-to belongs to the order's customer". */
IF OBJECT_ID(N'erp.CustomerLocation', N'U') IS NULL
CREATE TABLE erp.CustomerLocation
(
    LocationId  varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    CustomerId  varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    Label       nvarchar(200) NOT NULL,
    CONSTRAINT PK_CustomerLocation PRIMARY KEY CLUSTERED (LocationId),
    CONSTRAINT UQ_CustomerLocation_Location_Customer UNIQUE (LocationId, CustomerId),
    CONSTRAINT FK_CustomerLocation_Customer FOREIGN KEY (CustomerId)
        REFERENCES erp.Customer (CustomerId),
    CONSTRAINT CK_CustomerLocation_LocationId_NotBlank CHECK (LEN(LocationId) > 0)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.CustomerLocation') AND name = N'IX_CustomerLocation_CustomerId')
    CREATE INDEX IX_CustomerLocation_CustomerId ON erp.CustomerLocation (CustomerId);

/* Product: ProductId is the working model's canonical product identifier
   (ST1-H02). External identifiers live in erp.ProductIdentity. */
IF OBJECT_ID(N'erp.Product', N'U') IS NULL
CREATE TABLE erp.Product
(
    ProductId    varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    Description  nvarchar(500) NOT NULL,
    BaseUnit     varchar(32)   NOT NULL,
    CONSTRAINT PK_Product PRIMARY KEY CLUSTERED (ProductId),
    CONSTRAINT CK_Product_ProductId_NotBlank CHECK (LEN(ProductId) > 0)
);

/* Supplier ---------------------------------------------------------------- */
IF OBJECT_ID(N'erp.Supplier', N'U') IS NULL
CREATE TABLE erp.Supplier
(
    SupplierId  varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    Name        nvarchar(200) NOT NULL,
    CONSTRAINT PK_Supplier PRIMARY KEY CLUSTERED (SupplierId),
    CONSTRAINT CK_Supplier_SupplierId_NotBlank CHECK (LEN(SupplierId) > 0)
);

/* SupplierProduct: many suppliers per product and, by design, no uniqueness on
   (SupplierId, ProductId). The natural key is unresolved (D13), so the
   surrogate row key from the Stage 1 Toad projection is reused. */
IF OBJECT_ID(N'erp.SupplierProduct', N'U') IS NULL
CREATE TABLE erp.SupplierProduct
(
    SupplierProductRowId      int           IDENTITY(1,1) NOT NULL,
    SupplierId                varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId                 varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    SupplierItemNumber        varchar(100)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    PurchaseUnit              varchar(32)   NOT NULL,
    BaseUnitsPerPurchaseUnit  decimal(18,6) NOT NULL,
    CONSTRAINT PK_SupplierProduct PRIMARY KEY CLUSTERED (SupplierProductRowId),
    CONSTRAINT FK_SupplierProduct_Supplier FOREIGN KEY (SupplierId)
        REFERENCES erp.Supplier (SupplierId),
    CONSTRAINT FK_SupplierProduct_Product FOREIGN KEY (ProductId)
        REFERENCES erp.Product (ProductId),
    CONSTRAINT CK_SupplierProduct_BaseUnitsPerPurchaseUnit_Positive CHECK (BaseUnitsPerPurchaseUnit > 0)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.SupplierProduct') AND name = N'IX_SupplierProduct_SupplierId')
    CREATE INDEX IX_SupplierProduct_SupplierId ON erp.SupplierProduct (SupplierId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.SupplierProduct') AND name = N'IX_SupplierProduct_ProductId')
    CREATE INDEX IX_SupplierProduct_ProductId ON erp.SupplierProduct (ProductId);

/* Warehouse --------------------------------------------------------------- */
IF OBJECT_ID(N'erp.Warehouse', N'U') IS NULL
CREATE TABLE erp.Warehouse
(
    WarehouseId  varchar(64)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    Name         nvarchar(200) NOT NULL,
    CONSTRAINT PK_Warehouse PRIMARY KEY CLUSTERED (WarehouseId),
    CONSTRAINT CK_Warehouse_WarehouseId_NotBlank CHECK (LEN(WarehouseId) > 0)
);

/* InventoryPosition: association only — no quantities, reservations or
   allocations (Q24 unresolved). Stock on hand arrives in increment 2. */
IF OBJECT_ID(N'erp.InventoryPosition', N'U') IS NULL
CREATE TABLE erp.InventoryPosition
(
    WarehouseId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId    varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_InventoryPosition PRIMARY KEY CLUSTERED (WarehouseId, ProductId),
    CONSTRAINT FK_InventoryPosition_Warehouse FOREIGN KEY (WarehouseId)
        REFERENCES erp.Warehouse (WarehouseId),
    CONSTRAINT FK_InventoryPosition_Product FOREIGN KEY (ProductId)
        REFERENCES erp.Product (ProductId)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.InventoryPosition') AND name = N'IX_InventoryPosition_ProductId')
    CREATE INDEX IX_InventoryPosition_ProductId ON erp.InventoryPosition (ProductId);

PRINT N'020-erp-master-data: 7 master-data tables present.';
GO
