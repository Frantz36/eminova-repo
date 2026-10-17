// Eminova.Host — Data/EminovaDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace Eminova.Host.Data;

public sealed class EminovaDbContext : DbContext
{
    public EminovaDbContext(DbContextOptions<EminovaDbContext> options) : base(options) { }

    public DbSet<TenantState> TenantStates => Set<TenantState>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TenantState>(e =>
        {
            e.ToTable("eminova_state");
            e.HasKey(t => new { t.TenantId, t.UserId });
            e.Property(t => t.TenantId).HasMaxLength(64);
            e.Property(t => t.UserId).HasMaxLength(256);
            e.Property(t => t.Json).IsRequired();            // text / nvarchar(max) / jsonb-compatible text
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("eminova_audit");
            e.Property(a => a.TenantId).HasMaxLength(64);
            e.Property(a => a.UserId).HasMaxLength(256);
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.Detail).HasMaxLength(1024);
            e.HasIndex(a => new { a.TenantId, a.AtUtc });
        });

        b.Entity<ImportBatch>(e =>
        {
            e.ToTable("eminova_import");
            e.Property(i => i.TenantId).HasMaxLength(64);
            e.Property(i => i.UserId).HasMaxLength(256);
            e.Property(i => i.Entity).HasMaxLength(64);
            e.Property(i => i.FileName).HasMaxLength(260);
            e.Property(i => i.Warnings).HasMaxLength(4000);
            e.HasIndex(i => new { i.TenantId, i.AtUtc });
        });
    }
}
