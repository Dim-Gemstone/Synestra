using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    available_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    registered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_attempts_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    released_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_leases_job_attempts_job_attempt_id",
                        column: x => x.job_attempt_id,
                        principalTable: "job_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_leases_workers_worker_id",
                        column: x => x.worker_id,
                        principalTable: "workers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_job_id_number",
                table: "job_attempts",
                columns: new[] { "job_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_definitions_type",
                table: "job_definitions",
                column: "type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leases_job_attempt_id",
                table: "leases",
                column: "job_attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leases_worker_id",
                table: "leases",
                column: "worker_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_definitions");

            migrationBuilder.DropTable(
                name: "leases");

            migrationBuilder.DropTable(
                name: "job_attempts");

            migrationBuilder.DropTable(
                name: "workers");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
