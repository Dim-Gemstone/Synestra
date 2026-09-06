using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseRenewalAndExecutionCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "token_hash",
                table: "leases",
                type: "bytea",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "completion_report_id",
                table: "job_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "completion_snapshot",
                table: "job_attempts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result",
                table: "job_attempts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_leases_token_hash",
                table: "leases",
                sql: "token_hash IS NULL OR octet_length(token_hash) = 32");

            migrationBuilder.AddCheckConstraint(
                name: "CK_job_attempts_completion_report_id",
                table: "job_attempts",
                sql: "completion_report_id IS NULL OR (get_byte(uuid_send(completion_report_id), 6) >> 4 = 7 AND (get_byte(uuid_send(completion_report_id), 8) & 192) = 128)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_job_attempts_completion_snapshot",
                table: "job_attempts",
                sql: "(completion_report_id IS NULL) = (completion_snapshot IS NULL) AND (completion_snapshot IS NULL OR jsonb_typeof(completion_snapshot) = 'object')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_job_attempts_reported_completion",
                table: "job_attempts",
                sql: "completion_report_id IS NULL OR (\n  finished_at_utc IS NOT NULL AND finished_at_utc >= started_at_utc AND (\n    (status = 'Succeeded' AND result IS NOT NULL AND error_code IS NULL AND error_message IS NULL) OR\n    (status = 'Failed' AND result IS NULL AND error_code IS NOT NULL AND error_message IS NOT NULL\n      AND error_code ~ '[^[:space:]]' AND error_message ~ '[^[:space:]]')))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_job_attempts_result",
                table: "job_attempts",
                sql: "result IS NULL OR jsonb_typeof(result) = 'object'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_leases_token_hash",
                table: "leases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_job_attempts_completion_report_id",
                table: "job_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_job_attempts_completion_snapshot",
                table: "job_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_job_attempts_reported_completion",
                table: "job_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_job_attempts_result",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "leases");

            migrationBuilder.DropColumn(
                name: "completion_report_id",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "completion_snapshot",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "result",
                table: "job_attempts");
        }
    }
}
