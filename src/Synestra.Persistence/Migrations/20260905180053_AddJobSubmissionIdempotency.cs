using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synestra.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobSubmissionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_submissions",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_identity = table.Column<string>(type: "jsonb", nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_submissions", x => x.key);
                    table.CheckConstraint("CK_job_submissions_key_format", "key <> '' AND key COLLATE \"C\" !~ '[^!-~]' AND position(',' in key) = 0");
                    table.ForeignKey(
                        name: "FK_job_submissions_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_submissions_job_id",
                table: "job_submissions",
                column: "job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_submissions");
        }
    }
}
