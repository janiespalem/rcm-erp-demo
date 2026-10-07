using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rcm.Crm.Migrations;

[DbContext(typeof(CrmDb))]
[Migration("20260925180000_ReversibleCustomerArchive")]
public sealed class ReversibleCustomerArchive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ArchivedAt", table: "Customers", schema: "crm", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<long>(name: "ArchivedBy", table: "Customers", schema: "crm", type: "bigint", nullable: true);
        migrationBuilder.CreateIndex("IX_Customers_TeamId_ArchivedAt_DisplayName_Id", "Customers",
            new[] { "TeamId", "ArchivedAt", "DisplayName", "Id" }, schema: "crm");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("CRM archive metadata is retained on application rollback.");
}
