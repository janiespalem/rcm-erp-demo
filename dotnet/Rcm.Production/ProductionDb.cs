using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rcm.Production;

public sealed class ProductionContract
{
    public Guid Id { get; set; }
    public long Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Reference { get; set; } = "";
    public int? PlannedTetrapods { get; set; }
    public DateOnly? Deadline { get; set; }
    public DateOnly? OpeningDate { get; set; }
    public long? Opening6 { get; set; }
    public long? Opening12 { get; set; }
    public long? Opening16 { get; set; }
    public bool NormConfirmed { get; set; }
    public int BasketsPerTetrapod { get; set; } = 4;
    public long Norm6 { get; set; } = 3768;
    public long Norm12 { get; set; } = 3268;
    public long Norm16 { get; set; } = 49056;
    public bool IsSynthetic { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SteelDelivery
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public long Version { get; set; } = 1;
    public DateOnly DeliveryDate { get; set; }
    public long? Steel6 { get; set; }
    public long? Steel12 { get; set; }
    public long? Steel16 { get; set; }
    public string Note { get; set; } = "";
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProductionChange
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public long ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Changes { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReceipt
{
    public long ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReportLink
{
    public long ReportId { get; set; }
    public long Version { get; set; } = 1;
    public Guid ContractId { get; set; }
    public long SourceVersionAtLink { get; set; }
    public long LinkedBy { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class ProductionReportReview
{
    public Guid Id { get; set; }
    public long ReportId { get; set; }
    public long ReportVersion { get; set; }
    public long LinkVersion { get; set; }
    public Guid ContractId { get; set; }
    public long ReviewerId { get; set; }
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReportAudit
{
    public Guid Id { get; set; }
    public long ReportId { get; set; }
    public long ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Changes { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReviewReceipt
{
    public long ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReviewerAssignment
{
    public long UserId { get; set; }
    public long Version { get; set; } = 1;
    public bool Active { get; set; }
    public string ConfiguredBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class ProductionReviewerAssignmentAudit
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public long Version { get; set; }
    public bool? BeforeActive { get; set; }
    public bool Active { get; set; }
    public string ConfiguredBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionDb(DbContextOptions<ProductionDb> options) : DbContext(options)
{
    public DbSet<ProductionContract> Contracts => Set<ProductionContract>();
    public DbSet<SteelDelivery> Deliveries => Set<SteelDelivery>();
    public DbSet<ProductionChange> Changes => Set<ProductionChange>();
    public DbSet<ProductionReceipt> Receipts => Set<ProductionReceipt>();
    public DbSet<ProductionReportLink> ReportLinks => Set<ProductionReportLink>();
    public DbSet<ProductionReportReview> ReportReviews => Set<ProductionReportReview>();
    public DbSet<ProductionReportAudit> ReportReviewAudit => Set<ProductionReportAudit>();
    public DbSet<ProductionReviewReceipt> ReviewReceipts => Set<ProductionReviewReceipt>();
    public DbSet<ProductionReviewerAssignment> ReviewerAssignments => Set<ProductionReviewerAssignment>();
    public DbSet<ProductionReviewerAssignmentAudit> ReviewerAssignmentAudit => Set<ProductionReviewerAssignmentAudit>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("production");
        var c = m.Entity<ProductionContract>();
        c.ToTable("Contracts", t =>
        {
            t.HasCheckConstraint("CK_Contract_Fields", "length(btrim(\"Name\")) > 0 AND \"Version\" > 0 AND (\"PlannedTetrapods\" IS NULL OR \"PlannedTetrapods\" BETWEEN 0 AND 1000000)");
            t.HasCheckConstraint("CK_Contract_Opening", "(\"OpeningDate\" IS NOT NULL OR (\"Opening6\" IS NULL AND \"Opening12\" IS NULL AND \"Opening16\" IS NULL)) AND (\"Opening6\" IS NULL OR \"Opening6\" BETWEEN 0 AND 1000000000000) AND (\"Opening12\" IS NULL OR \"Opening12\" BETWEEN 0 AND 1000000000000) AND (\"Opening16\" IS NULL OR \"Opening16\" BETWEEN 0 AND 1000000000000)");
            t.HasCheckConstraint("CK_Contract_Norm", "\"BasketsPerTetrapod\" > 0 AND \"Norm6\" > 0 AND \"Norm12\" > 0 AND \"Norm16\" > 0");
        });
        c.Property(x => x.Name).HasMaxLength(240);
        c.Property(x => x.Reference).HasMaxLength(240);
        c.Property(x => x.Version).IsConcurrencyToken();
        c.HasIndex(x => new { x.Name, x.Id });
        var d = m.Entity<SteelDelivery>();
        d.ToTable("Deliveries", t => t.HasCheckConstraint("CK_Delivery_Fields", "\"Version\" > 0 AND (\"Steel6\" > 0 OR \"Steel12\" > 0 OR \"Steel16\" > 0) IS TRUE AND (\"Steel6\" IS NULL OR \"Steel6\" BETWEEN 0 AND 1000000000000) AND (\"Steel12\" IS NULL OR \"Steel12\" BETWEEN 0 AND 1000000000000) AND (\"Steel16\" IS NULL OR \"Steel16\" BETWEEN 0 AND 1000000000000)"));
        d.Property(x => x.Version).IsConcurrencyToken();
        d.Property(x => x.Note).HasMaxLength(8000);
        d.HasOne<ProductionContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        d.HasIndex(x => new { x.ContractId, x.DeliveryDate, x.Id });
        var a = m.Entity<ProductionChange>();
        a.ToTable("Changes");
        a.Property(x => x.Action).HasMaxLength(80);
        a.Property(x => x.Reason).HasMaxLength(2000);
        a.Property(x => x.Changes).HasColumnType("jsonb");
        a.HasOne<ProductionContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        a.HasIndex(x => new { x.ContractId, x.RecordedAt, x.Id });
        var r = m.Entity<ProductionReceipt>();
        r.ToTable("Receipts");
        r.HasKey(x => new { x.ActorId, x.RequestId });
        r.Property(x => x.Fingerprint).HasMaxLength(64);
        r.Property(x => x.Result).HasColumnType("jsonb");
        var l = m.Entity<ProductionReportLink>();
        l.HasKey(x => x.ReportId);
        l.Property(x => x.ReportId).ValueGeneratedNever();
        l.ToTable("ReportLinks", t => t.HasCheckConstraint("CK_ReportLink_Version", "\"ReportId\" > 0 AND \"Version\" > 0 AND \"SourceVersionAtLink\" > 0 AND \"LinkedBy\" > 0"));
        l.Property(x => x.Version).IsConcurrencyToken();
        l.HasOne<ProductionContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        l.HasIndex(x => new { x.ContractId, x.ReportId });
        var review = m.Entity<ProductionReportReview>();
        review.ToTable("ReportReviews", t => t.HasCheckConstraint("CK_ReportReview_Decision", "\"ReportId\" > 0 AND \"ReportVersion\" > 0 AND \"LinkVersion\" > 0 AND \"ReviewerId\" > 0 AND \"Decision\" IN ('accepted','returned') AND (\"Decision\" <> 'returned' OR length(btrim(\"Reason\")) > 0)"));
        review.Property(x => x.Decision).HasMaxLength(20);
        review.Property(x => x.Reason).HasMaxLength(2000);
        review.Property(x => x.Snapshot).HasColumnType("jsonb");
        review.HasOne<ProductionContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        review.HasIndex(x => new { x.ReportId, x.ReportVersion, x.LinkVersion }).IsUnique();
        var audit = m.Entity<ProductionReportAudit>();
        audit.ToTable("ReportReviewAudit");
        audit.Property(x => x.Action).HasMaxLength(40);
        audit.Property(x => x.Reason).HasMaxLength(2000);
        audit.Property(x => x.Changes).HasColumnType("jsonb");
        audit.HasIndex(x => new { x.ReportId, x.RecordedAt, x.Id });
        var receipt = m.Entity<ProductionReviewReceipt>();
        receipt.ToTable("ReviewReceipts");
        receipt.HasKey(x => new { x.ActorId, x.RequestId });
        receipt.Property(x => x.Fingerprint).HasMaxLength(64);
        receipt.Property(x => x.Result).HasColumnType("jsonb");
        var assignment = m.Entity<ProductionReviewerAssignment>();
        assignment.ToTable("ReviewerAssignments", t => t.HasCheckConstraint("CK_ReviewerAssignment_Fields", "\"UserId\" > 0 AND \"Version\" > 0 AND length(btrim(\"ConfiguredBy\")) > 0 AND length(btrim(\"Reason\")) > 0"));
        assignment.HasKey(x => x.UserId);
        assignment.Property(x => x.UserId).ValueGeneratedNever();
        assignment.Property(x => x.Version).IsConcurrencyToken();
        assignment.Property(x => x.ConfiguredBy).HasMaxLength(240);
        assignment.Property(x => x.Reason).HasMaxLength(2000);
        var assignmentAudit = m.Entity<ProductionReviewerAssignmentAudit>();
        assignmentAudit.ToTable("ReviewerAssignmentAudit");
        assignmentAudit.Property(x => x.ConfiguredBy).HasMaxLength(240);
        assignmentAudit.Property(x => x.Reason).HasMaxLength(2000);
        assignmentAudit.HasIndex(x => new { x.UserId, x.Version }).IsUnique();
    }
}

public sealed class ProductionDbFactory : IDesignTimeDbContextFactory<ProductionDb>
{
    public ProductionDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ProductionDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("PRODUCTION_DATABASE") ?? "Host=localhost;Database=rcm_production_design",
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "production")).Options);
}
