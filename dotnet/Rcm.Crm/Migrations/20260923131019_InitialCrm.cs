using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rcm.Crm.Migrations
{
    public partial class InitialCrm : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.CreateTable(
                name: "Receipts",
                schema: "crm",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => new { x.TeamId, x.ActorId, x.RequestId });
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ContactPerson = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Phone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    NormalizedPhone = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    OriginalNote = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SearchText = table.Column<string>(type: "text", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.UniqueConstraint("AK_Customers_TeamId_Id", x => new { x.TeamId, x.Id });
                    table.ForeignKey(
                        name: "FK_Customers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "crm",
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                schema: "crm",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Memberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "crm",
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerChanges",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Changes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerChanges_Customers_TeamId_CustomerId",
                        columns: x => new { x.TeamId, x.CustomerId },
                        principalSchema: "crm",
                        principalTable: "Customers",
                        principalColumns: new[] { "TeamId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Topics",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Products = table.Column<string[]>(type: "text[]", nullable: false),
                    Need = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NextDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextAction = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Topics", x => x.Id);
                    table.UniqueConstraint("AK_Topics_TeamId_Id", x => new { x.TeamId, x.Id });
                    table.CheckConstraint("CK_Topic_Plan", "NOT (\"NextDate\" IS NOT NULL AND \"NextDescription\" IS NOT NULL) AND (\"State\" <> 'closed' OR (\"NextDate\" IS NULL AND \"NextDescription\" IS NULL AND \"NextAction\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_Topics_Customers_TeamId_CustomerId",
                        columns: x => new { x.TeamId, x.CustomerId },
                        principalSchema: "crm",
                        principalTable: "Customers",
                        principalColumns: new[] { "TeamId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    ActorName = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    NextDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextDescription = table.Column<string>(type: "text", nullable: true),
                    NextAction = table.Column<string>(type: "text", nullable: true),
                    Changes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Topics_TeamId_TopicId",
                        columns: x => new { x.TeamId, x.TopicId },
                        principalSchema: "crm",
                        principalTable: "Topics",
                        principalColumns: new[] { "TeamId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerChanges_TeamId_CustomerId",
                schema: "crm",
                table: "CustomerChanges",
                columns: new[] { "TeamId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TeamId_DisplayName_Id",
                schema: "crm",
                table: "Customers",
                columns: new[] { "TeamId", "DisplayName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TeamId_NormalizedEmail",
                schema: "crm",
                table: "Customers",
                columns: new[] { "TeamId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TeamId_NormalizedPhone",
                schema: "crm",
                table: "Customers",
                columns: new[] { "TeamId", "NormalizedPhone" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TeamId_TopicId_RecordedAt_Id",
                schema: "crm",
                table: "Events",
                columns: new[] { "TeamId", "TopicId", "RecordedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TeamId",
                schema: "crm",
                table: "Memberships",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Topics_TeamId_CustomerId",
                schema: "crm",
                table: "Topics",
                columns: new[] { "TeamId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Topics_TeamId_NextDate_Id",
                schema: "crm",
                table: "Topics",
                columns: new[] { "TeamId", "NextDate", "Id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("CRM data is retained on application rollback. Restore a verified backup only through the recovery procedure.");
    }
}
