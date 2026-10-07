using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rcm.Crm;

public sealed class Team
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
public sealed class Membership
{
    public long UserId { get; set; }
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
}
public sealed class Customer
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public long Version { get; set; } = 1;
    public string DisplayName { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? NormalizedPhone { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? Source { get; set; }
    public string? OriginalNote { get; set; }
    public string SearchText { get; set; } = "";
    public bool IsSynthetic { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public long? ArchivedBy { get; set; }
    public List<Topic> Topics { get; set; } = [];
}
public sealed class Topic
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string[] Products { get; set; } = [];
    public string? Need { get; set; }
    public string State { get; set; } = "new";
    public DateOnly? NextDate { get; set; }
    public string? NextDescription { get; set; }
    public string? NextAction { get; set; }
    public List<ContactEvent> Events { get; set; } = [];
}
public sealed class ContactEvent
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid TopicId { get; set; }
    public Topic Topic { get; set; } = null!;
    public long ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public string Kind { get; set; } = "";
    public string? Note { get; set; }
    public string State { get; set; } = "";
    public DateOnly? NextDate { get; set; }
    public string? NextDescription { get; set; }
    public string? NextAction { get; set; }
    public string? Changes { get; set; }
}
public sealed class CustomerChange
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public long ActorId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Changes { get; set; } = "";
}
public sealed class CommandReceipt
{
    public Guid TeamId { get; set; }
    public long ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class CrmDb(DbContextOptions<CrmDb> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<ContactEvent> Events => Set<ContactEvent>();
    public DbSet<CustomerChange> CustomerChanges => Set<CustomerChange>();
    public DbSet<CommandReceipt> Receipts => Set<CommandReceipt>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("crm");
        m.Entity<Team>().Property(x => x.Name).HasMaxLength(160);
        m.Entity<Membership>().HasKey(x => x.UserId);
        m.Entity<Membership>().HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        var c = m.Entity<Customer>();
        c.HasKey(x => x.Id);
        c.HasAlternateKey(x => new { x.TeamId, x.Id });
        c.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        c.Property(x => x.Version).IsConcurrencyToken();
        c.Property(x => x.DisplayName).HasMaxLength(240);
        c.Property(x => x.ContactPerson).HasMaxLength(240);
        c.Property(x => x.Phone).HasMaxLength(80);
        c.Property(x => x.Email).HasMaxLength(320);
        c.Property(x => x.Source).HasMaxLength(160);
        c.Property(x => x.OriginalNote).HasMaxLength(8000);
        c.HasIndex(x => new { x.TeamId, x.DisplayName, x.Id });
        c.HasIndex(x => new { x.TeamId, x.ArchivedAt, x.DisplayName, x.Id });
        c.HasIndex(x => new { x.TeamId, x.NormalizedPhone });
        c.HasIndex(x => new { x.TeamId, x.NormalizedEmail });
        var t = m.Entity<Topic>();
        t.HasKey(x => x.Id);
        t.HasAlternateKey(x => new { x.TeamId, x.Id });
        t.HasOne(x => x.Customer).WithMany(x => x.Topics).HasForeignKey(x => new { x.TeamId, x.CustomerId })
            .HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        t.Property(x => x.Version).IsConcurrencyToken();
        t.Property(x => x.Need).HasMaxLength(8000);
        t.Property(x => x.State).HasMaxLength(40);
        t.Property(x => x.NextDescription).HasMaxLength(1000);
        t.Property(x => x.NextAction).HasMaxLength(1000);
        t.HasIndex(x => new { x.TeamId, x.NextDate, x.Id });
        t.ToTable("Topics", b => b.HasCheckConstraint("CK_Topic_Plan", "NOT (\"NextDate\" IS NOT NULL AND \"NextDescription\" IS NOT NULL) AND (\"State\" <> 'closed' OR (\"NextDate\" IS NULL AND \"NextDescription\" IS NULL AND \"NextAction\" IS NULL))"));
        var e = m.Entity<ContactEvent>();
        e.HasKey(x => x.Id);
        e.HasOne(x => x.Topic).WithMany(x => x.Events).HasForeignKey(x => new { x.TeamId, x.TopicId })
            .HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.TeamId, x.TopicId, x.RecordedAt, x.Id });
        e.Property(x => x.Note).HasMaxLength(8000);
        var a = m.Entity<CustomerChange>();
        a.HasKey(x => x.Id);
        a.HasOne(x => x.Customer).WithMany().HasForeignKey(x => new { x.TeamId, x.CustomerId })
            .HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        m.Entity<CommandReceipt>().HasKey(x => new { x.TeamId, x.ActorId, x.RequestId });
    }
}
public sealed class CrmDbFactory : IDesignTimeDbContextFactory<CrmDb>
{
    public CrmDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CrmDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("CRM_DATABASE") ?? "Host=localhost;Database=rcm_crm_design",
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "crm")).Options);
}
