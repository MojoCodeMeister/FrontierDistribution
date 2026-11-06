namespace Frontier.Domain;

// Stage 1 logical entities: these are NOT Frontier's actual database records.
public sealed record Supplier(string SupplierId, string Name);
public sealed record Product(string ProductId, string Description, string BaseUnit);
public sealed record Customer(string CustomerId, string Name);
public sealed record CustomerLocation(string LocationId, string CustomerId, string Label);
public sealed record Warehouse(string WarehouseId, string Name);
public sealed record SupplierProduct(string SupplierId, string ProductId, string SupplierItemNumber, string PurchaseUnit, decimal BaseUnitsPerPurchaseUnit);
public sealed record InventoryPosition(string WarehouseId, string ProductId);
public sealed record ProductIdentity(string ProductId, string System, string IdentifierType, string ExternalId, string? Context = null);
