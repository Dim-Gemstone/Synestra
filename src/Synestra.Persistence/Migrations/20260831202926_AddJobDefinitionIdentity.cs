using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobDefinitionIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "job_definition_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE jobs AS j
                SET job_definition_id = d.id
                FROM job_definitions AS d
                WHERE j.type = d.type;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "job_definition_id",
                table: "jobs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_job_definitions_id_type",
                table: "job_definitions",
                columns: new[] { "id", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_job_definition_id_type",
                table: "jobs",
                columns: new[] { "job_definition_id", "type" });

            migrationBuilder.AddForeignKey(
                name: "FK_jobs_job_definitions_job_definition_id_type",
                table: "jobs",
                columns: new[] { "job_definition_id", "type" },
                principalTable: "job_definitions",
                principalColumns: new[] { "id", "type" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_jobs_job_definitions_job_definition_id_type",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "IX_jobs_job_definition_id_type",
                table: "jobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_job_definitions_id_type",
                table: "job_definitions");

            migrationBuilder.DropColumn(
                name: "job_definition_id",
                table: "jobs");
        }
    }
}
