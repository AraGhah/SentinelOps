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
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<ServiceDependency> ServiceDependencies => Set<ServiceDependency>();
    public DbSet<ServiceAlertRule> ServiceAlertRules => Set<ServiceAlertRule>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    public DbSet<IncidentStatusHistory> IncidentStatusHistories => Set<IncidentStatusHistory>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<IncidentTag> IncidentTags => Set<IncidentTag>();
    public DbSet<RelatedIncidentLink> RelatedIncidentLinks => Set<RelatedIncidentLink>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<IngestionRequestRecord> IngestionRequestRecords => Set<IngestionRequestRecord>();
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationLevel> EscalationLevels => Set<EscalationLevel>();
    public DbSet<EscalationLevelTarget> EscalationLevelTargets => Set<EscalationLevelTarget>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleRotation> ScheduleRotations => Set<ScheduleRotation>();
    public DbSet<ScheduleOverride> ScheduleOverrides => Set<ScheduleOverride>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ProcessedWorkerEvent> ProcessedWorkerEvents => Set<ProcessedWorkerEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();

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

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Action).HasMaxLength(200);
            b.Property(a => a.EntityType).HasMaxLength(100);
            b.Property(a => a.IpAddress).HasMaxLength(64);
            b.Property(a => a.UserAgent).HasMaxLength(500);
            b.Property(a => a.Details).HasColumnType("jsonb");
            b.HasIndex(a => new { a.OrganizationId, a.CreatedAtUtc });
            // Unlike ITenantOwned entities, AuditLog.OrganizationId is nullable, so this
            // filter is hand-written here rather than derived from the ITenantOwned
            // marker interface: it must still admit org-less (e.g. login) entries.
            b.HasQueryFilter(a => a.OrganizationId == null || a.OrganizationId == currentOrganization.OrganizationId);
        });

        modelBuilder.Entity<Service>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.Description).HasMaxLength(2000);
            b.HasIndex(s => new { s.OrganizationId, s.Name });
            b.HasQueryFilter(s => s.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(s => s.Organization).WithMany().HasForeignKey(s => s.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceDependency>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.ServiceId, d.DependsOnServiceId }).IsUnique();
            b.HasQueryFilter(d => d.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(d => d.Service).WithMany().HasForeignKey(d => d.ServiceId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(d => d.DependsOnService).WithMany().HasForeignKey(d => d.DependsOnServiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ServiceAlertRule>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.Description).HasMaxLength(2000);
            b.Property(r => r.Condition).HasMaxLength(500);
            b.HasIndex(r => new { r.OrganizationId, r.ServiceId });
            b.HasQueryFilter(r => r.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(r => r.Service).WithMany().HasForeignKey(r => r.ServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Integration>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Name).HasMaxLength(200);
            b.Property(i => i.Provider).HasMaxLength(100);
            b.Property(i => i.ApiKeyHash).HasMaxLength(128);
            b.Property(i => i.ApiKeyLastFour).HasMaxLength(4);
            b.Property(i => i.SigningSecret).HasMaxLength(64);
            b.HasIndex(i => new { i.OrganizationId, i.ServiceId });
            b.HasQueryFilter(i => i.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(i => i.Organization).WithMany().HasForeignKey(i => i.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Service).WithMany().HasForeignKey(i => i.ServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Incident>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Title).HasMaxLength(300);
            b.HasIndex(i => new { i.OrganizationId, i.Status });
            b.HasIndex(i => new { i.OrganizationId, i.ServiceId });
            b.HasQueryFilter(i => i.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(i => i.Organization).WithMany().HasForeignKey(i => i.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Service).WithMany().HasForeignKey(i => i.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IncidentComment>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasIndex(c => new { c.OrganizationId, c.IncidentId });
            b.HasQueryFilter(c => c.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(c => c.Incident).WithMany().HasForeignKey(c => c.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentStatusHistory>(b =>
        {
            b.HasKey(h => h.Id);
            b.HasIndex(h => new { h.OrganizationId, h.IncidentId });
            b.HasQueryFilter(h => h.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(h => h.Incident).WithMany().HasForeignKey(h => h.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentEvent>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Summary).HasMaxLength(500);
            b.Property(e => e.Details).HasColumnType("jsonb");
            // Ascending, since the timeline endpoint always renders oldest-first.
            b.HasIndex(e => new { e.OrganizationId, e.IncidentId, e.OccurredAtUtc });
            b.HasQueryFilter(e => e.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(e => e.Incident).WithMany().HasForeignKey(e => e.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentTag>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Tag).HasMaxLength(100);
            b.HasIndex(t => new { t.OrganizationId, t.IncidentId, t.Tag }).IsUnique();
            b.HasQueryFilter(t => t.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(t => t.Incident).WithMany().HasForeignKey(t => t.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RelatedIncidentLink>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.IncidentId, l.RelatedIncidentId }).IsUnique();
            b.HasQueryFilter(l => l.OrganizationId == currentOrganization.OrganizationId);

            // Both endpoints reference Incident; only one FK may cascade or Postgres
            // rejects the multiple-cascade-path configuration (same issue as
            // ServiceDependency above).
            b.HasOne<Incident>().WithMany().HasForeignKey(l => l.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Incident>().WithMany().HasForeignKey(l => l.RelatedIncidentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Alert>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.ExternalId).HasMaxLength(200);
            b.Property(a => a.Source).HasMaxLength(200);
            b.Property(a => a.Title).HasMaxLength(300);
            b.Property(a => a.Environment).HasMaxLength(100);
            b.Property(a => a.Region).HasMaxLength(100);
            b.Property(a => a.Metadata).HasColumnType("jsonb");
            b.Property(a => a.RawPayload).HasColumnType("jsonb");
            b.HasIndex(a => new { a.OrganizationId, a.ExternalId });
            b.HasIndex(a => new { a.OrganizationId, a.IncidentId });
            b.HasQueryFilter(a => a.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(a => a.Organization).WithMany().HasForeignKey(a => a.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Integration).WithMany().HasForeignKey(a => a.IntegrationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Incident).WithMany().HasForeignKey(a => a.IncidentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestionRequestRecord>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.IdempotencyKey).HasMaxLength(300);
            b.HasIndex(r => new { r.IntegrationId, r.IdempotencyKey }).IsUnique();
            b.HasQueryFilter(r => r.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne<Integration>().WithMany().HasForeignKey(r => r.IntegrationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EscalationPolicy>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).HasMaxLength(200);
            b.Property(p => p.Description).HasMaxLength(2000);
            b.HasIndex(p => new { p.OrganizationId, p.ServiceId });
            b.HasQueryFilter(p => p.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(p => p.Organization).WithMany().HasForeignKey(p => p.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Service).WithMany().HasForeignKey(p => p.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EscalationLevel>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.EscalationPolicyId, l.Order }).IsUnique();
            b.HasQueryFilter(l => l.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(l => l.EscalationPolicy).WithMany(p => p.Levels).HasForeignKey(l => l.EscalationPolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EscalationLevelTarget>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.EscalationLevelId, t.UserId }).IsUnique();
            b.HasQueryFilter(t => t.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(t => t.EscalationLevel).WithMany(l => l.Targets).HasForeignKey(t => t.EscalationLevelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Schedule>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.TimeZoneId).HasMaxLength(100);
            b.HasIndex(s => new { s.OrganizationId, s.ServiceId });
            b.HasQueryFilter(s => s.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(s => s.Organization).WithMany().HasForeignKey(s => s.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(s => s.Service).WithMany().HasForeignKey(s => s.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ScheduleRotation>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.ScheduleId, r.DayOfWeek });
            b.HasQueryFilter(r => r.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(r => r.Schedule).WithMany().HasForeignKey(r => r.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduleOverride>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.Reason).HasMaxLength(500);
            b.HasIndex(o => new { o.ScheduleId, o.StartsAtUtc, o.EndsAtUtc });
            b.HasQueryFilter(o => o.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(o => o.Schedule).WithMany().HasForeignKey(o => o.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Report>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.HtmlStorageKey).HasMaxLength(500);
            b.Property(r => r.PdfStorageKey).HasMaxLength(500);
            b.HasIndex(r => new { r.OrganizationId, r.IncidentId });
            b.HasQueryFilter(r => r.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(r => r.Incident).WithMany().HasForeignKey(r => r.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Attachment>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.FileName).HasMaxLength(260);
            b.Property(a => a.ContentType).HasMaxLength(150);
            b.Property(a => a.StorageKey).HasMaxLength(500);
            b.HasIndex(a => new { a.OrganizationId, a.IncidentId });
            b.HasQueryFilter(a => a.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(a => a.Incident).WithMany().HasForeignKey(a => a.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessedWorkerEvent>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.WorkerName).HasMaxLength(100);
            b.HasIndex(e => new { e.WorkerName, e.EventId }).IsUnique();
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.HasKey(n => n.Id);
            b.Property(n => n.Channel).HasMaxLength(50);
            b.Property(n => n.FailureReason).HasMaxLength(1000);
            b.HasIndex(n => new { n.OrganizationId, n.IncidentId });
            b.HasQueryFilter(n => n.OrganizationId == currentOrganization.OrganizationId);

            b.HasOne(n => n.Incident).WithMany().HasForeignKey(n => n.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.TimeZoneId).HasMaxLength(100);
            b.HasIndex(p => new { p.OrganizationId, p.UserId }).IsUnique();
            b.HasQueryFilter(p => p.OrganizationId == currentOrganization.OrganizationId);
        });

        modelBuilder.Entity<AnalyticsEvent>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.EventType).HasMaxLength(100);
            b.HasIndex(e => new { e.OrganizationId, e.EventType, e.OccurredAtUtc });
        });
    }
}
