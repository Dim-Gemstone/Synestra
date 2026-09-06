using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseSessionBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "leases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_leases_worker_id_session_id",
                table: "leases",
                columns: new[] { "worker_id", "session_id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_leases_session_id",
                table: "leases",
                sql: "session_id IS NULL OR (get_byte(uuid_send(session_id), 6) >> 4 = 7 AND (get_byte(uuid_send(session_id), 8) & 192) = 128)");

            migrationBuilder.AddForeignKey(
                name: "FK_leases_worker_sessions_worker_id_session_id",
                table: "leases",
                columns: new[] { "worker_id", "session_id" },
                principalTable: "worker_sessions",
                principalColumns: new[] { "worker_id", "session_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_leases_worker_sessions_worker_id_session_id",
                table: "leases");

            migrationBuilder.DropIndex(
                name: "IX_leases_worker_id_session_id",
                table: "leases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_leases_session_id",
                table: "leases");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "leases");
        }
    }
}
