using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;

namespace SentinelOps.Api.Tenancy;

// Resolves the organization from the "orgId" route value, looks up the caller's
// active membership for it, and — on success — populates ICurrentOrganizationAccessor
// so the DbContext's tenant query filters see the same organization the authorization
// check just verified. This is the single place that decides "is this org id + this
// caller allowed," so controllers never need to re-check membership themselves.
public class OrganizationRoleAuthorizationHandler(
    SentinelOpsDbContext db,
    ICurrentUserService currentUserService,
    ICurrentOrganizationAccessor currentOrganization,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<OrganizationRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OrganizationRoleRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var routeOrgId = httpContext?.Request.RouteValues["orgId"]?.ToString();
        if (!Guid.TryParse(routeOrgId, out var organizationId))
        {
            context.Fail(new AuthorizationFailureReason(this, "Request is missing a valid organization id."));
            return;
        }

        var user = await currentUserService.GetOrProvisionAsync(httpContext?.RequestAborted ?? default);

        var membership = await db.OrganizationMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.OrganizationId == organizationId && m.UserId == user.Id,
                httpContext?.RequestAborted ?? default);

        if (membership is null || !membership.IsActive)
        {
            context.Fail(new AuthorizationFailureReason(this, "Caller is not an active member of this organization."));
            return;
        }

        if (membership.Role < requirement.MinimumRole)
        {
            context.Fail(new AuthorizationFailureReason(this, "Caller's role does not meet the required minimum."));
            return;
        }

        currentOrganization.Set(organizationId, membership.Role, membership.Id);
        context.Succeed(requirement);
    }
}
