using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Table mapping (TRP 3.11.2). The schema itself is created by the InitialCreate migration.</summary>
public sealed class TcriDbContext : DbContext
{
    public TcriDbContext(DbContextOptions<TcriDbContext> options) : base(options) { }

    public DbSet<CdrRow> Cdrs => Set<CdrRow>();
    public DbSet<SourceStateRow> SourceStates => Set<SourceStateRow>();
    public DbSet<InboxRow> InboxItems => Set<InboxRow>();
    public DbSet<RejectedRow> RejectedItems => Set<RejectedRow>();
    public DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();
    public DbSet<OutboxRow> OutboxItems => Set<OutboxRow>();
    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<ErrorLogRow> ErrorLog => Set<ErrorLogRow>();

    /// <summary>Inserts '_' before each uppercase letter except the first and lowercases everything.</summary>
    internal static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // 1. Table = class name without the Row suffix in snake_case; column = property name in snake_case.
        foreach (var entity in b.Model.GetEntityTypes().ToList())
        {
            var name = entity.ClrType.Name;
            if (name.EndsWith("Row", StringComparison.Ordinal)) name = name[..^3];
            b.Entity(entity.ClrType).ToTable(SnakeCase(name));
            foreach (var p in entity.GetProperties())
                p.SetColumnName(SnakeCase(p.Name));
        }

        // 2. Keys.
        b.Entity<CdrRow>().HasKey(e => e.CdrSeq);
        b.Entity<CdrRow>().Property(e => e.CdrSeq).ValueGeneratedOnAdd();
        b.Entity<SourceStateRow>().HasKey(e => new { e.TenantId, e.CallId });
        b.Entity<InboxRow>().HasKey(e => e.Id);
        b.Entity<InboxRow>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<RejectedRow>().HasKey(e => e.Id);
        b.Entity<RejectedRow>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<AuditLogRow>().HasKey(e => e.Id);
        b.Entity<AuditLogRow>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<OutboxRow>().HasKey(e => e.Id);
        b.Entity<OutboxRow>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<ErrorLogRow>().HasKey(e => e.Id);
        b.Entity<ErrorLogRow>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<SubscriptionRow>().HasKey(e => e.Id);
        b.Entity<SubscriptionRow>().Property(e => e.Id).ValueGeneratedNever();

        // 3. Indexes and unique constraints use the DDL names.
        b.Entity<CdrRow>().HasIndex(e => new { e.TenantId, e.CallId, e.SessionId }, "uq_cdr_key").IsUnique().HasDatabaseName("uq_cdr_key");
        b.Entity<CdrRow>().HasIndex(e => new { e.StartUtc, e.CdrSeq }, "ix_cdr_start").HasDatabaseName("ix_cdr_start");
        b.Entity<CdrRow>().HasIndex(e => new { e.TenantId, e.CallId }, "ix_cdr_call").HasDatabaseName("ix_cdr_call");
        b.Entity<SourceStateRow>().HasIndex(e => new { e.State, e.NextAttemptUtc }, "ix_source_state_due").HasDatabaseName("ix_source_state_due");
        b.Entity<InboxRow>().HasIndex(e => e.ElementHash, "uq_inbox_new_hash").IsUnique().HasFilter("state = 'new'").HasDatabaseName("uq_inbox_new_hash");
        b.Entity<InboxRow>().HasIndex(e => e.ElementHash, "ix_inbox_hash").HasDatabaseName("ix_inbox_hash");
        b.Entity<InboxRow>().HasIndex(e => new { e.TenantId, e.CallId, e.State }, "ix_inbox_call").HasDatabaseName("ix_inbox_call");
        b.Entity<InboxRow>().HasIndex(e => new { e.State, e.NextAttemptUtc }, "ix_inbox_due").HasDatabaseName("ix_inbox_due");
        b.Entity<RejectedRow>().HasIndex(e => e.CreatedUtc, "ix_rejected_created").HasDatabaseName("ix_rejected_created");
        b.Entity<OutboxRow>().HasIndex(e => new { e.Kind, e.TenantId, e.CallId, e.SessionId, e.Recipient }, "uq_outbox_key").IsUnique().HasDatabaseName("uq_outbox_key");
        b.Entity<OutboxRow>().HasIndex(e => new { e.State, e.NextAttemptUtc }, "ix_outbox_due").HasDatabaseName("ix_outbox_due");
        b.Entity<ErrorLogRow>().HasIndex(e => e.Ts, "ix_error_log_ts").HasDatabaseName("ix_error_log_ts");

        // 4. CHECK constraints are not configured here: only InitialCreate.sql creates them.
    }
}
