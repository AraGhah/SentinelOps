using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Data;

public class SentinelOpsDbContext(DbContextOptions<SentinelOpsDbContext> options, ICurrentOrganizationAccessor currentOrganization)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.CognitoSub).IsUnique();
            b.Property(u => u.CognitoSub).HasMaxLength(128);
            b.Property(u => u.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<Organization>(b =>
        {
            b.HasKey(o => o.Id);
            b.HasIndex(o => o.Slug).IsUnique();
            b.Property(o => o.Name).HasMaxLength(200);
            b.Property(o => o.Slug).HasMaxLength(200);

            b.HasOne(o => o.Settings)
                .WithOne()
                .HasForeignKey<OrganizationSettings>(s => s.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ITenantOwned entities below get `WHERE OrganizationId = @currentOrgId`
        // applied automatically. currentOrganization.OrganizationId is null until
        // an authorization handler has verified the caller's membership for a
        // specific organization, so tenant-owned rows are invisible by default
        // instead of relying on every query site to remember to filter.
        modelBuilder.Entity<OrganizationSettings>(b =>
        {
            b.HasKey(s => s.OrganizationId);
            b.HasQueryFilter(s => s.OrganizationId == currentOrganization.OrganizationId);
        });

        modelBuilder.Entity<OrganizationMembership>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique();
            b.HasQueryFilter(m => m.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(m => m.Organization)
                .WithMany(o => o.Memberships)
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(m => m.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationInvitation>(b =>
        {
            b.HasKey(i => i.Id);
            b.HasIndex(i => i.Token).IsUnique();
            b.HasIndex(i => new { i.OrganizationId, i.Email, i.Status });
            b.Property(i => i.Email).HasMaxLength(320);
            b.Property(i => i.Token).HasMaxLength(64);
            b.HasQueryFilter(i => i.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(i => i.Organization)
                .WithMany(o => o.Invitations)
                .HasForeignKey(i => i.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
