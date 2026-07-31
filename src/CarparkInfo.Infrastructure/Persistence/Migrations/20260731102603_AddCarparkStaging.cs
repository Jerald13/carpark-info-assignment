using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarparkInfo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCarparkStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carpark_staging",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    job_run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    car_park_no = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    svy21_x = table.Column<double>(type: "REAL", precision: 12, scale: 4, nullable: false),
                    svy21_y = table.Column<double>(type: "REAL", precision: 12, scale: 4, nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: false),
                    longitude = table.Column<double>(type: "REAL", nullable: false),
                    car_park_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    parking_system_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    short_term_parking_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    free_parking_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    has_night_parking = table.Column<bool>(type: "INTEGER", nullable: false),
                    deck_count = table.Column<int>(type: "INTEGER", nullable: false),
                    gantry_height_m = table.Column<decimal>(type: "TEXT", precision: 4, scale: 2, nullable: true),
                    has_height_restriction = table.Column<bool>(type: "INTEGER", nullable: false),
                    gantry_height_raw = table.Column<decimal>(type: "TEXT", precision: 4, scale: 2, nullable: false),
                    has_basement = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_row_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    line_number = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carpark_staging", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staging_run_carpark",
                table: "carpark_staging",
                columns: new[] { "job_run_id", "car_park_no" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carpark_staging");
        }
    }
}
