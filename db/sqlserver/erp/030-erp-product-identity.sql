/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 030: Product identity (cross-application product identifier map)

    Source record: src/Frontier.Domain/Entities.cs (ProductIdentity)
    Rules:         src/Frontier.Identity/IdentityCatalog.cs, ProductIdentityResolver.cs
    Owner:         ERP (decision D3, approved 2026-11-11)

    An external identifier is unique only within its complete scope:
    (System, IdentifierType, ExternalId, Context), compared case-insensitively.
    A NULL Context is its own scope, distinct from any supplied Context.
    SQL Server unique constraints treat NULLs as equal, which is exactly that rule:
    two NULL-context duplicates collide; NULL and 'SUP-1' do not.

    Resolution (Found / NotFound / Ambiguous) stays in the C# resolver; this
    script adds no lookup function. The scope index serves resolver queries.
    Synthetic model. Not Frontier's production database. Rerunnable.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'erp.Product', N'U') IS NULL
    THROW 50001, N'Table erp.Product is missing. Run 020-erp-master-data.sql first.', 1;

IF OBJECT_ID(N'erp.ProductIdentity', N'U') IS NULL
CREATE TABLE erp.ProductIdentity
(
    ProductIdentityRowId  int          IDENTITY(1,1) NOT NULL,
    ProductId             varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    [System]              varchar(80)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    IdentifierType        varchar(80)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    ExternalId            varchar(150) COLLATE Latin1_General_100_CI_AS NOT NULL,
    [Context]             varchar(150) COLLATE Latin1_General_100_CI_AS NULL,
    CONSTRAINT PK_ProductIdentity PRIMARY KEY CLUSTERED (ProductIdentityRowId),
    CONSTRAINT UQ_ProductIdentity_Scope UNIQUE ([System], IdentifierType, ExternalId, [Context]),
    CONSTRAINT FK_ProductIdentity_Product FOREIGN KEY (ProductId)
        REFERENCES erp.Product (ProductId),
    CONSTRAINT CK_ProductIdentity_System_NotBlank CHECK (LEN([System]) > 0),
    CONSTRAINT CK_ProductIdentity_IdentifierType_NotBlank CHECK (LEN(IdentifierType) > 0),
    CONSTRAINT CK_ProductIdentity_ExternalId_NotBlank CHECK (LEN(ExternalId) > 0)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.ProductIdentity') AND name = N'IX_ProductIdentity_ProductId')
    CREATE INDEX IX_ProductIdentity_ProductId ON erp.ProductIdentity (ProductId);

PRINT N'030-erp-product-identity: erp.ProductIdentity present.';
GO
