using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Integrations;

public record CreateIntegrationRequest([Required, MaxLength(200)] string Name, [Required, MaxLength(100)] string Provider);

public record IntegrationResponse(
    Guid Id, Guid ServiceId, string Name, string Provider, string ApiKeyLastFour, IntegrationStatus Status,
    DateTimeOffset? LastUsedAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset? RevokedAtUtc);

// Returned only once, at creation/rotation time — the plaintext key is never stored
// or retrievable again afterwards.
public record IntegrationCreatedResponse(IntegrationResponse Integration, string ApiKey);

public record IntegrationFilters(IntegrationStatus? Status);
