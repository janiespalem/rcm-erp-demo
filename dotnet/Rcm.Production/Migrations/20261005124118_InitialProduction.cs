using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rcm.Production.Migrations
{
    public partial class InitialProduction : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "production");

            migrationBuilder.CreateTable(
                name: "Contracts",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Reference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    PlannedTetrapods = table.Column<int>(type: "integer", nullable: true),
                    Deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    OpeningDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Opening6 = table.Column<long>(type: "bigint", nullable: true),
                    Opening12 = table.Column<long>(type: "bigint", nullable: true),
                    Opening16 = table.Column<long>(type: "bigint", nullable: true),
                    NormConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    BasketsPerTetrapod = table.Column<int>(type: "integer", nullable: false),
                    Norm6 = table.Column<long>(type: "bigint", nullable: false),
                    Norm12 = table.Column<long>(type: "bigint", nullable: false),
                    Norm16 = table.Column<long>(type: "bigint", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.CheckConstraint("CK_Contract_Fields", "length(btrim(\"Name\")) > 0 AND \"Version\" > 0 AND (\"PlannedTetrapods\" IS NULL OR \"PlannedTetrapods\" BETWEEN 0 AND 1000000)");
                    table.CheckConstraint("CK_Contract_Norm", "\"BasketsPerTetrapod\" > 0 AND \"Norm6\" > 0 AND \"Norm12\" > 0 AND \"Norm16\" > 0");
                    table.CheckConstraint("CK_Contract_Opening", "(\"OpeningDate\" IS NOT NULL OR (\"Opening6\" IS NULL AND \"Opening12\" IS NULL AND \"Opening16\" IS NULL)) AND (\"Opening6\" IS NULL OR \"Opening6\" BETWEEN 0 AND 1000000000000) AND (\"Opening12\" IS NULL OR \"Opening12\" BETWEEN 0 AND 1000000000000) AND (\"Opening16\" IS NULL OR \"Opening16\" BETWEEN 0 AND 1000000000000)");
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
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
                    table.PrimaryKey("PK_Receipts", x => new { x.ActorId, x.RequestId });
                });

            migrationBuilder.CreateTable(
                name: "Changes",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Changes = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Changes_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "production",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "production",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Steel6 = table.Column<long>(type: "bigint", nullable: true),
                    Steel12 = table.Column<long>(type: "bigint", nullable: true),
                    Steel16 = table.Column<long>(type: "bigint", nullable: true),
                    Note = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.CheckConstraint("CK_Delivery_Fields", "\"Version\" > 0 AND (\"Steel6\" > 0 OR \"Steel12\" > 0 OR \"Steel16\" > 0) IS TRUE AND (\"Steel6\" IS NULL OR \"Steel6\" BETWEEN 0 AND 1000000000000) AND (\"Steel12\" IS NULL OR \"Steel12\" BETWEEN 0 AND 1000000000000) AND (\"Steel16\" IS NULL OR \"Steel16\" BETWEEN 0 AND 1000000000000)");
                    table.ForeignKey(
                        name: "FK_Deliveries_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalSchema: "production",
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Changes_ContractId_RecordedAt_Id",
                schema: "production",
                table: "Changes",
                columns: new[] { "ContractId", "RecordedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Name_Id",
                schema: "production",
                table: "Contracts",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ContractId_DeliveryDate_Id",
                schema: "production",
                table: "Deliveries",
                columns: new[] { "ContractId", "DeliveryDate", "Id" });

            migrationBuilder.Sql("""
                CREATE FUNCTION production.protect_contract_identity() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, production AS $$
                BEGIN
                    IF (NEW."Id", NEW."BasketsPerTetrapod", NEW."Norm6", NEW."Norm12", NEW."Norm16", NEW."IsSynthetic", NEW."CreatedAt")
                        IS DISTINCT FROM (OLD."Id", OLD."BasketsPerTetrapod", OLD."Norm6", OLD."Norm12", OLD."Norm16", OLD."IsSynthetic", OLD."CreatedAt") THEN
                        RAISE EXCEPTION 'Contract identity and norm snapshot are immutable' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Version" <> OLD."Version" + 1 THEN
                        RAISE EXCEPTION 'Contract version must increase by one' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_contract_identity BEFORE UPDATE ON production."Contracts"
                    FOR EACH ROW EXECUTE FUNCTION production.protect_contract_identity();
                CREATE FUNCTION production.protect_delivery_identity() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, production AS $$
                BEGIN
                    IF (NEW."Id", NEW."ContractId", NEW."CreatedBy", NEW."CreatedAt")
                        IS DISTINCT FROM (OLD."Id", OLD."ContractId", OLD."CreatedBy", OLD."CreatedAt") THEN
                        RAISE EXCEPTION 'Delivery identity is immutable' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Version" <> OLD."Version" + 1 THEN
                        RAISE EXCEPTION 'Delivery version must increase by one' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_delivery_identity BEFORE UPDATE ON production."Deliveries"
                    FOR EACH ROW EXECUTE FUNCTION production.protect_delivery_identity();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("Production history is preserved; use a forward migration.");
    }
}
