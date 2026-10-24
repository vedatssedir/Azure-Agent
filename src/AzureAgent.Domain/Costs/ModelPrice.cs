using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Costs;

/// <summary>Price list per model, in USD per one million tokens. Organization rows override defaults.</summary>
public sealed class ModelPrice : Entity
{
    /// <summary>Null = instance default.</summary>
    public Guid? OrganizationId { get; init; }
    public ModelProvider Provider { get; init; }
    public required string Model { get; init; }
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public decimal CachedInputPerMillion { get; set; }

    public decimal Cost(long input, long output, long cachedInput)
    {
        var billableInput = Math.Max(0, input - cachedInput);
        return (billableInput * InputPerMillion + cachedInput * CachedInputPerMillion + output * OutputPerMillion) / 1_000_000m;
    }
}
