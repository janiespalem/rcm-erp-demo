using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Rcm.Production;

#nullable disable

namespace Rcm.Production.Migrations
{
    [DbContext(typeof(ProductionDb))]
    [Migration("20261005124118_InitialProduction")]
    partial class InitialProduction
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasDefaultSchema("production")
                .HasAnnotation("ProductVersion", "10.0.12")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Rcm.Production.ProductionChange", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("Action")
                        .IsRequired()
                        .HasMaxLength(80)
                        .HasColumnType("character varying(80)");

                    b.Property<long>("ActorId")
                        .HasColumnType("bigint");

                    b.Property<string>("Changes")
                        .IsRequired()
                        .HasColumnType("jsonb");

                    b.Property<Guid>("ContractId")
                        .HasColumnType("uuid");

                    b.Property<string>("Reason")
                        .IsRequired()
                        .HasMaxLength(2000)
                        .HasColumnType("character varying(2000)");

                    b.Property<DateTimeOffset>("RecordedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("ContractId", "RecordedAt", "Id");

                    b.ToTable("Changes", "production");
                });

            modelBuilder.Entity("Rcm.Production.ProductionContract", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<int>("BasketsPerTetrapod")
                        .HasColumnType("integer");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateOnly?>("Deadline")
                        .HasColumnType("date");

                    b.Property<bool>("IsSynthetic")
                        .HasColumnType("boolean");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(240)
                        .HasColumnType("character varying(240)");

                    b.Property<long>("Norm12")
                        .HasColumnType("bigint");

                    b.Property<long>("Norm16")
                        .HasColumnType("bigint");

                    b.Property<long>("Norm6")
                        .HasColumnType("bigint");

                    b.Property<bool>("NormConfirmed")
                        .HasColumnType("boolean");

                    b.Property<long?>("Opening12")
                        .HasColumnType("bigint");

                    b.Property<long?>("Opening16")
                        .HasColumnType("bigint");

                    b.Property<long?>("Opening6")
                        .HasColumnType("bigint");

                    b.Property<DateOnly?>("OpeningDate")
                        .HasColumnType("date");

                    b.Property<int?>("PlannedTetrapods")
                        .HasColumnType("integer");

                    b.Property<string>("Reference")
                        .IsRequired()
                        .HasMaxLength(240)
                        .HasColumnType("character varying(240)");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<long>("Version")
                        .IsConcurrencyToken()
                        .HasColumnType("bigint");

                    b.HasKey("Id");

                    b.HasIndex("Name", "Id");

                    b.ToTable("Contracts", "production", t =>
                        {
                            t.HasCheckConstraint("CK_Contract_Fields", "length(btrim(\"Name\")) > 0 AND \"Version\" > 0 AND (\"PlannedTetrapods\" IS NULL OR \"PlannedTetrapods\" BETWEEN 0 AND 1000000)");

                            t.HasCheckConstraint("CK_Contract_Norm", "\"BasketsPerTetrapod\" > 0 AND \"Norm6\" > 0 AND \"Norm12\" > 0 AND \"Norm16\" > 0");

                            t.HasCheckConstraint("CK_Contract_Opening", "(\"OpeningDate\" IS NOT NULL OR (\"Opening6\" IS NULL AND \"Opening12\" IS NULL AND \"Opening16\" IS NULL)) AND (\"Opening6\" IS NULL OR \"Opening6\" BETWEEN 0 AND 1000000000000) AND (\"Opening12\" IS NULL OR \"Opening12\" BETWEEN 0 AND 1000000000000) AND (\"Opening16\" IS NULL OR \"Opening16\" BETWEEN 0 AND 1000000000000)");
                        });
                });

            modelBuilder.Entity("Rcm.Production.ProductionReceipt", b =>
                {
                    b.Property<long>("ActorId")
                        .HasColumnType("bigint");

                    b.Property<Guid>("RequestId")
                        .HasColumnType("uuid");

                    b.Property<string>("Fingerprint")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.Property<DateTimeOffset>("RecordedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Result")
                        .IsRequired()
                        .HasColumnType("jsonb");

                    b.HasKey("ActorId", "RequestId");

                    b.ToTable("Receipts", "production");
                });

            modelBuilder.Entity("Rcm.Production.SteelDelivery", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<Guid>("ContractId")
                        .HasColumnType("uuid");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<long>("CreatedBy")
                        .HasColumnType("bigint");

                    b.Property<DateOnly>("DeliveryDate")
                        .HasColumnType("date");

                    b.Property<string>("Note")
                        .IsRequired()
                        .HasMaxLength(8000)
                        .HasColumnType("character varying(8000)");

                    b.Property<long?>("Steel12")
                        .HasColumnType("bigint");

                    b.Property<long?>("Steel16")
                        .HasColumnType("bigint");

                    b.Property<long?>("Steel6")
                        .HasColumnType("bigint");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<long>("UpdatedBy")
                        .HasColumnType("bigint");

                    b.Property<long>("Version")
                        .IsConcurrencyToken()
                        .HasColumnType("bigint");

                    b.HasKey("Id");

                    b.HasIndex("ContractId", "DeliveryDate", "Id");

                    b.ToTable("Deliveries", "production", t =>
                        {
                            t.HasCheckConstraint("CK_Delivery_Fields", "\"Version\" > 0 AND (\"Steel6\" > 0 OR \"Steel12\" > 0 OR \"Steel16\" > 0) IS TRUE AND (\"Steel6\" IS NULL OR \"Steel6\" BETWEEN 0 AND 1000000000000) AND (\"Steel12\" IS NULL OR \"Steel12\" BETWEEN 0 AND 1000000000000) AND (\"Steel16\" IS NULL OR \"Steel16\" BETWEEN 0 AND 1000000000000)");
                        });
                });

            modelBuilder.Entity("Rcm.Production.ProductionChange", b =>
                {
                    b.HasOne("Rcm.Production.ProductionContract", null)
                        .WithMany()
                        .HasForeignKey("ContractId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();
                });

            modelBuilder.Entity("Rcm.Production.SteelDelivery", b =>
                {
                    b.HasOne("Rcm.Production.ProductionContract", null)
                        .WithMany()
                        .HasForeignKey("ContractId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
