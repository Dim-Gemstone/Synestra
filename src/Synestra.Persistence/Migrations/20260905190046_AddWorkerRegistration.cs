using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "session_started_at_utc",
                table: "workers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "worker_sessions",
                columns: table => new
                {
                    worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_sessions", x => new { x.worker_id, x.session_id });
                    table.ForeignKey(
                        name: "FK_worker_sessions_workers_worker_id",
                        column: x => x.worker_id,
                        principalTable: "workers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "worker_supported_types",
                columns: table => new
                {
                    worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_supported_types", x => new { x.worker_id, x.type });
                    table.CheckConstraint("CK_worker_supported_types_type", "type ~ '[^[:space:]]'");
                    table.ForeignKey(
                        name: "FK_worker_supported_types_workers_worker_id",
                        column: x => x.worker_id,
                        principalTable: "workers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_capacity",
                table: "workers",
                sql: "capacity > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_session",
                table: "workers",
                sql: "(session_id IS NULL) = (session_started_at_utc IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_sessions");

            migrationBuilder.DropTable(
                name: "worker_supported_types");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_capacity",
                table: "workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_session",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "session_started_at_utc",
                table: "workers");
        }
    }
}
