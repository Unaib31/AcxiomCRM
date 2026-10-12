using System.Text.Json;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AcxiomCRM.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp), nameof(ApplicationUser.ConcurrencyStamp)
    };
    private static readonly JsonSerializerOptions AuditJson = new() { WriteIndented = false };
    private readonly ICurrentUser _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser? currentUser = null) : base(options)
        => _currentUser = currentUser ?? new SystemUser();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ManagerId);
            // SQLite cannot compare DateTimeOffset in SQL; store as unix milliseconds so lockout queries translate.
            e.Property(x => x.LockoutEnd).HasConversion(
                v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : (long?)null,
                v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : (DateTimeOffset?)null);
        });
        b.Entity<ApplicationRole>(e => e.Property(x => x.Description).HasMaxLength(300));

        b.Entity<Customer>(e =>
        {
            e.HasKey(x => x.CustomerId);
            e.Property(x => x.CustomerCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.CustomerName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.CompanyName).HasMaxLength(150);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.State).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.CustomerCode).IsUnique();
            e.HasIndex(x => x.Email).IsUnique().HasFilter("\"IsDeleted\" = 0");
            e.HasIndex(x => x.Phone).IsUnique().HasFilter("\"IsDeleted\" = 0");
            e.HasIndex(x => x.AssignedTo);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.AssignedTo).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Lead>(e =>
        {
            e.HasKey(x => x.LeadId);
            e.Property(x => x.LeadCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.LeadName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.CompanyName).HasMaxLength(150);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            Money(e.Property(x => x.ExpectedValue));
            e.HasIndex(x => x.LeadCode).IsUnique();
            e.HasIndex(x => x.AssignedTo);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.AssignedTo).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConvertedCustomer).WithMany().HasForeignKey(x => x.ConvertedCustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Opportunity>(e =>
        {
            e.HasKey(x => x.OpportunityId);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.WeightedAmount);
            e.Property(x => x.OpportunityCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.OpportunityName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.OutcomeNotes).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            Money(e.Property(x => x.Amount));
            e.HasIndex(x => x.OpportunityCode).IsUnique();
            e.HasIndex(x => x.AssignedTo);
            e.HasIndex(x => x.Stage);
            e.HasIndex(x => x.CustomerId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.AssignedTo).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted && !x.Customer!.IsDeleted);
        });

        b.Entity<FollowUp>(e =>
        {
            e.HasKey(x => x.FollowUpId);
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.Property(x => x.Remarks).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FollowUpType).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.AssignedTo, x.Status, x.FollowUpDate });
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.AssignedTo).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Activity>(e =>
        {
            e.HasKey(x => x.ActivityId);
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.ActivityType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.AssignedTo, x.ActivityDate });
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.AssignedTo).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.AuditLogId);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Module).HasMaxLength(50).IsRequired();
            e.Property(x => x.Action).HasMaxLength(50).IsRequired();
            e.Property(x => x.EntityName).HasMaxLength(100);
            e.Property(x => x.RecordId).HasMaxLength(100);
            e.Property(x => x.OldValue).HasMaxLength(4000);
            e.Property(x => x.NewValue).HasMaxLength(4000);
            e.Property(x => x.Result).HasMaxLength(20).IsRequired();
            e.Property(x => x.Details).HasMaxLength(1000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.CreatedDate);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.Module, x.Action });
            e.HasIndex(x => new { x.EntityName, x.RecordId });
        });

        b.Entity<PasswordHistory>(e =>
        {
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<RevokedToken>(e =>
        {
            e.HasKey(x => x.TokenId);
            e.Property(x => x.TokenId).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresUtc);
        });
    }

    /// <summary>Stored as REAL so SQL aggregation/ordering work; precision is declared for other providers.</summary>
    private static void Money(PropertyBuilder<decimal> p) => p.HasPrecision(18, 2).HasConversion<double>();

    // ------------------------------------------------------------------ audit

    private sealed record Pending(EntityEntry Entry, EntityState State, string Module, string? Old, string? New, string? Action, string? Details);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        ChangeTracker.DetectChanges();
        EnforceAuditLogAppendOnly();

        var pending = CaptureAuditable();
        var needsCode = ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Entity is IHasCode).ToList();
        if (pending.Count == 0 && needsCode.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        var ownTx = Database.CurrentTransaction is null;
        await using var tx = ownTx ? await Database.BeginTransactionAsync(ct) : null;

        foreach (var e in needsCode)
            if (string.IsNullOrEmpty(((IHasCode)e.Entity).Code)) ((IHasCode)e.Entity).Code = "TMP-" + Guid.NewGuid().ToString("N");

        var affected = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        // Codes derive from the generated key, so they are assigned right after the insert (same transaction).
        var codesAssigned = false;
        foreach (var e in needsCode)
        {
            var key = e.Properties.First(p => p.Metadata.IsPrimaryKey()).CurrentValue;
            var prefix = e.Entity switch { Customer => "CUS", Lead => "LD", Opportunity => "OPP", _ => "REC" };
            ((IHasCode)e.Entity).Code = $"{prefix}-{Convert.ToInt32(key):D6}";
            codesAssigned = true;
        }
        if (codesAssigned) await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        if (pending.Count > 0)
        {
            foreach (var p in pending)
            {
                // Added entries only know their key and final values after the insert.
                var isAdd = p.State == EntityState.Added;
                AuditLogs.Add(new AuditLog
                {
                    UserId = _currentUser.UserId,
                    UserName = _currentUser.UserName,
                    Module = p.Module,
                    Action = p.Action ?? p.State switch { EntityState.Added => "Create", EntityState.Deleted => "Delete", _ => "Update" },
                    EntityName = p.Entry.Metadata.ClrType.Name,
                    RecordId = KeyOf(p.Entry),
                    OldValue = p.Old,
                    NewValue = isAdd ? Serialize(p.Entry.Properties.Where(x => !IsSkipped(x)).ToDictionary(x => x.Metadata.Name, x => Plain(x.CurrentValue))) : p.New,
                    Details = p.Details,
                    Result = "Success",
                    CreatedDate = DateTime.UtcNow,
                    IpAddress = _currentUser.IpAddress,
                });
            }
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }

        if (tx is not null) await tx.CommitAsync(ct);
        return affected;
    }

    private void EnforceAuditLogAppendOnly()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit log entries are append-only.");
    }

    private List<Pending> CaptureAuditable()
    {
        var list = new List<Pending>();
        foreach (var e in ChangeTracker.Entries().Where(x => x.Entity is IAuditable && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            var module = ModuleOf(e.Entity);
            switch (e.State)
            {
                case EntityState.Added:
                    list.Add(new Pending(e, e.State, module, null, null, null, null));
                    break;
                case EntityState.Deleted:
                    list.Add(new Pending(e, e.State, module, Serialize(e.Properties.Where(p => !IsSkipped(p)).ToDictionary(p => p.Metadata.Name, p => Plain(p.OriginalValue))), null, null, null));
                    break;
                case EntityState.Modified:
                    var changed = e.Properties.Where(p => p.IsModified && !IsSkipped(p) && !Equals(p.OriginalValue, p.CurrentValue)).ToList();
                    if (changed.Count == 0) break;
                    string? action = null, details = null;
                    var softDeleted = changed.FirstOrDefault(p => p.Metadata.Name == "IsDeleted" && p.CurrentValue is true);
                    var status = changed.FirstOrDefault(p => p.Metadata.Name is "Status" or "Stage");
                    var date = changed.FirstOrDefault(p => p.Metadata.Name == "FollowUpDate");
                    var notes = new List<string>();
                    if (status is not null) notes.Add($"{status.Metadata.Name}: {Plain(status.OriginalValue)} → {Plain(status.CurrentValue)}");
                    if (date is not null) notes.Add($"Date: {Plain(date.OriginalValue)} → {Plain(date.CurrentValue)}");
                    if (notes.Count > 0) details = string.Join("; ", notes);
                    if (softDeleted is not null) action = "Delete";
                    else if (status is not null) action = "StatusChange";
                    else if (date is not null) action = "Reschedule";
                    list.Add(new Pending(e, e.State, module,
                        Serialize(changed.ToDictionary(p => p.Metadata.Name, p => Plain(p.OriginalValue))),
                        Serialize(changed.ToDictionary(p => p.Metadata.Name, p => Plain(p.CurrentValue))), action, details));
                    break;
            }
        }
        return list;
    }

    private static string ModuleOf(object entity) => entity switch
    {
        Customer => "Customers", Lead => "Leads", Opportunity => "Opportunities",
        FollowUp => "FollowUps", Activity => "Activities", _ => entity.GetType().Name
    };

    private static bool IsSkipped(PropertyEntry p) => SensitiveProperties.Contains(p.Metadata.Name);

    private static string KeyOf(EntityEntry e) =>
        string.Join("-", e.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue));

    private static object? Plain(object? v) => v switch
    {
        null => null,
        Enum en => en.ToString(),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => v
    };

    private static string Serialize(Dictionary<string, object?> d)
    {
        var json = JsonSerializer.Serialize(d, AuditJson);
        return json.Length <= 4000 ? json : json[..3990] + "…\"}";
    }
}
