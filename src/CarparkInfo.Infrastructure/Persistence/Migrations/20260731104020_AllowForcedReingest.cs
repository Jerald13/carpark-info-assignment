using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarparkInfo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowForcedReingest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_job_run_file_hash",
                table: "job_run");

            migrationBuilder.CreateIndex(
                name: "ix_job_run_file_hash",
                table: "job_run",
                columns: new[] { "file_hash", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_job_run_file_hash",
                table: "job_run");

            migrationBuilder.CreateIndex(
                name: "ux_job_run_file_hash",
                table: "job_run",
                column: "file_hash",
                unique: true,
                filter: "status = 'Succeeded'");
        }
    }
}
