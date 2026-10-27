using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flash.Deals.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "deals");

            migrationBuilder.CreateTable(
                name: "Deals",
                schema: "deals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Aisle = table.Column<string>(type: "text", nullable: false),
                    Brand = table.Column<string>(type: "text", nullable: false),
                    SaleDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    PiecesTotal = table.Column<int>(type: "integer", nullable: false),
                    PiecesLeft = table.Column<int>(type: "integer", nullable: false),
                    MaxPerShopper = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deals", x => x.Id);
                    table.CheckConstraint("CK_Deals_Cap", "\"MaxPerShopper\" >= 1");
                    table.CheckConstraint("CK_Deals_Remaining", "\"PiecesLeft\" >= 0 AND \"PiecesLeft\" <= \"PiecesTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "deals",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "deals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DealShoppers",
                schema: "deals",
                columns: table => new
                {
                    DealId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pieces = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DealShoppers", x => new { x.DealId, x.OwnerId });
                    table.CheckConstraint("CK_DealShoppers_Pieces", "\"Pieces\" >= 0");
                    table.ForeignKey(
                        name: "FK_DealShoppers_Deals_DealId",
                        column: x => x.DealId,
                        principalSchema: "deals",
                        principalTable: "Deals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PieceHolds",
                schema: "deals",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DealId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pieces = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PieceHolds", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_PieceHolds_Deals_DealId",
                        column: x => x.DealId,
                        principalSchema: "deals",
                        principalTable: "Deals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deals_Code",
                schema: "deals",
                table: "Deals",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "deals",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PieceHolds_DealId",
                schema: "deals",
                table: "PieceHolds",
                column: "DealId");

            migrationBuilder.CreateIndex(
                name: "IX_PieceHolds_Status_ExpiresAt",
                schema: "deals",
                table: "PieceHolds",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DealShoppers",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "PieceHolds",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "Deals",
                schema: "deals");
        }
    }
}
