using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rcm.Production.Migrations
{
    public partial class ProductionReportReview : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportLinks",
                schema: "production",
                columns: table => new
                {
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersionAtLink = table.Column<long>(type: "bigint", nullable: false),
                    LinkedBy = table.Column<long>(type: "bigint", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportLinks", x => x.ReportId);
                    table.CheckConstraint("CK_ReportLink_Version", "\"ReportId\" > 0 AND \"Version\" > 0 AND \"SourceVersionAtLink\" > 0 AND \"LinkedBy\" > 0");
                    table.ForeignKey(
                        name: "FK_ReportLinks_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "production",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportReviewAudit",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Changes = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportReviewAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportReviews",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    ReportVersion = table.Column<long>(type: "bigint", nullable: false),
                    LinkVersion = table.Column<long>(type: "bigint", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportReviews", x => x.Id);
                    table.CheckConstraint("CK_ReportReview_Decision", "\"ReportId\" > 0 AND \"ReportVersion\" > 0 AND \"LinkVersion\" > 0 AND \"ReviewerId\" > 0 AND \"Decision\" IN ('accepted','returned') AND (\"Decision\" <> 'returned' OR length(btrim(\"Reason\")) > 0)");
                    table.ForeignKey(
                        name: "FK_ReportReviews_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "production",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewerAssignmentAudit",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    BeforeActive = table.Column<bool>(type: "boolean", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    ConfiguredBy = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewerAssignmentAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReviewerAssignments",
                schema: "production",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    ConfiguredBy = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewerAssignments", x => x.UserId);
                    table.CheckConstraint("CK_ReviewerAssignment_Fields", "\"UserId\" > 0 AND \"Version\" > 0 AND length(btrim(\"ConfiguredBy\")) > 0 AND length(btrim(\"Reason\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "ReviewReceipts",
                schema: "production",
                columns: table => new
                {
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewReceipts", x => new { x.ActorId, x.RequestId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportLinks_ContractId_ReportId",
                schema: "production",
                table: "ReportLinks",
                columns: new[] { "ContractId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportReviewAudit_ReportId_RecordedAt_Id",
                schema: "production",
                table: "ReportReviewAudit",
                columns: new[] { "ReportId", "RecordedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportReviews_ContractId",
                schema: "production",
                table: "ReportReviews",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportReviews_ReportId_ReportVersion_LinkVersion",
                schema: "production",
                table: "ReportReviews",
                columns: new[] { "ReportId", "ReportVersion", "LinkVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewerAssignmentAudit_UserId_Version",
                schema: "production",
                table: "ReviewerAssignmentAudit",
                columns: new[] { "UserId", "Version" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION production.serialize_reviewer_assignment() RETURNS trigger
                LANGUAGE plpgsql SET search_path=pg_catalog,production AS $$
                BEGIN
                    IF TG_OP='DELETE' THEN
                        RAISE EXCEPTION 'Revoke a reviewer assignment without deleting history' USING ERRCODE='23514';
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtextextended('production-reviewer:' || NEW."UserId"::text,0));
                    IF TG_OP='UPDATE' AND (NEW."UserId" <> OLD."UserId" OR NEW."Version" <> OLD."Version"+1) THEN
                        RAISE EXCEPTION 'Assignment identity is immutable and version must increase by one' USING ERRCODE='23514';
                    END IF;
                    IF TG_OP='INSERT' AND NEW."Version"<>1 THEN
                        RAISE EXCEPTION 'New reviewer assignment starts at version one' USING ERRCODE='23514';
                    END IF;
                    NEW."ChangedAt" := clock_timestamp();
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER serialize_reviewer_assignment BEFORE INSERT OR UPDATE OR DELETE ON production."ReviewerAssignments"
                    FOR EACH ROW EXECUTE FUNCTION production.serialize_reviewer_assignment();
                CREATE FUNCTION production.record_reviewer_assignment() RETURNS trigger
                LANGUAGE plpgsql SET search_path=pg_catalog,production AS $$
                BEGIN
                    INSERT INTO production."ReviewerAssignmentAudit"("Id","UserId","Version","BeforeActive","Active","ConfiguredBy","Reason","RecordedAt")
                    VALUES(gen_random_uuid(),NEW."UserId",NEW."Version",CASE WHEN TG_OP='UPDATE' THEN OLD."Active" ELSE NULL END,
                        NEW."Active",NEW."ConfiguredBy",NEW."Reason",NEW."ChangedAt");
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER record_reviewer_assignment AFTER INSERT OR UPDATE ON production."ReviewerAssignments"
                    FOR EACH ROW EXECUTE FUNCTION production.record_reviewer_assignment();
                CREATE FUNCTION production.preserve_review_history() RETURNS trigger
                LANGUAGE plpgsql SET search_path=pg_catalog,production AS $$
                BEGIN
                    RAISE EXCEPTION 'Production review history is immutable' USING ERRCODE='23514';
                END;
                $$;
                CREATE TRIGGER preserve_review_history BEFORE UPDATE OR DELETE ON production."ReportReviews"
                    FOR EACH ROW EXECUTE FUNCTION production.preserve_review_history();
                CREATE TRIGGER preserve_review_history BEFORE UPDATE OR DELETE ON production."ReportReviewAudit"
                    FOR EACH ROW EXECUTE FUNCTION production.preserve_review_history();
                CREATE TRIGGER preserve_review_history BEFORE UPDATE OR DELETE ON production."ReviewReceipts"
                    FOR EACH ROW EXECUTE FUNCTION production.preserve_review_history();
                CREATE TRIGGER preserve_review_history BEFORE UPDATE OR DELETE ON production."ReviewerAssignmentAudit"
                    FOR EACH ROW EXECUTE FUNCTION production.preserve_review_history();
                CREATE FUNCTION production.protect_report_link() RETURNS trigger
                LANGUAGE plpgsql SET search_path=pg_catalog,production AS $$
                BEGIN
                    IF TG_OP='DELETE' THEN
                        RAISE EXCEPTION 'Report links retain history' USING ERRCODE='23514';
                    END IF;
                    IF NEW."ReportId"<>OLD."ReportId" OR NEW."Version"<>OLD."Version"+1 THEN
                        RAISE EXCEPTION 'Report link identity is immutable and version must increase by one' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_report_link BEFORE UPDATE OR DELETE ON production."ReportLinks"
                    FOR EACH ROW EXECUTE FUNCTION production.protect_report_link();
                """);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("Report review and assignment history are preserved; use a forward migration.");
    }
}
