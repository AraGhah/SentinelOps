using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Tenancy;

// Resolves (and lazily provisions) the local User row for the caller's
// Cognito identity. Cognito access tokens carry `sub` and `username`
// (username == email in this pool, since sign-up uses email as the
// username) but not `email` itself, so we mirror AuthController's fallback.
public interface ICurrentUserService
{
    Task<User> GetOrProvisionAsync(CancellationToken ct = default);
}

public class CurrentUserService(SentinelOpsDbContext db, IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    private User? _cached;

    public async Task<User> GetOrProvisionAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var principal = httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No HTTP context available.");

        var sub = principal.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("Authenticated principal is missing a 'sub' claim.");
        var email = principal.FindFirst("email")?.Value
            ?? principal.FindFirst("username")?.Value
            ?? throw new InvalidOperationException("Authenticated principal is missing an email/username claim.");

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.CognitoSub == sub, ct);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                CognitoSub = sub,
                Email = email,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        else if (user.Email != email)
        {
            user.Email = email;
            await db.SaveChangesAsync(ct);
        }

        _cached = user;
        return user;
    }
}
