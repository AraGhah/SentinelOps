using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Tenancy;

public static class OrgPolicies
{
    public const string Viewer = "OrgRole:Viewer";
    public const string Responder = "OrgRole:Responder";
    public const string Administrator = "OrgRole:Administrator";
    public const string Owner = "OrgRole:Owner";

    public static string ForRole(OrganizationRole role) => $"OrgRole:{role}";
}
