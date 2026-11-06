using Frontier.Domain;

namespace Frontier.Contracts;

public enum ResolveStatus { Found, NotFound, Ambiguous }
public sealed record ProductIdentityQuery(string System, string IdentifierType, string ExternalId, string? Context = null);
public sealed record ProductResolution(ResolveStatus Status, Product? Product, IReadOnlyList<string> CandidateProductIds)
{
    public static ProductResolution Missing() => new(ResolveStatus.NotFound, null, Array.Empty<string>());
}
public interface IProductIdentityResolver
{
    ProductResolution Resolve(ProductIdentityQuery query);
}
