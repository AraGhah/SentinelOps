using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Organizations;

public record CreateOrganizationRequest(
    [Required, MinLength(2), MaxLength(200)] string Name);

public record OrganizationResponse(Guid Id, string Name, string Slug, DateTimeOffset CreatedAtUtc);

public record MyOrganizationResponse(Guid Id, string Name, string Slug, OrganizationRole Role, bool IsCurrent);

public record OrganizationSettingsResponse(
    string TimeZone, string? AlertNotificationEmail, bool RequireMfaForMembers, DateTimeOffset UpdatedAtUtc);

public record UpdateOrganizationSettingsRequest(
    [Required, MaxLength(100)] string TimeZone,
    [EmailAddress, MaxLength(320)] string? AlertNotificationEmail,
    bool RequireMfaForMembers);

public record MemberResponse(
    Guid MembershipId, Guid UserId, string Email, OrganizationRole Role, bool IsActive, DateTimeOffset CreatedAtUtc);

public record UpdateMemberRoleRequest([Required] OrganizationRole Role);

public record CreateInvitationRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required] OrganizationRole Role);

public record InvitationResponse(
    Guid Id, string Email, OrganizationRole Role, InvitationStatus Status, DateTimeOffset ExpiresAtUtc, string Token);

public record AcceptInvitationRequest([Required, MaxLength(64)] string Token);
