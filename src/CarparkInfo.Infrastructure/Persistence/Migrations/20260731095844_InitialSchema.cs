using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CarparkInfo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    failed_login_count = table.Column<int>(type: "INTEGER", nullable: false),
                    lockout_ends_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "car_park_type",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_car_park_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "free_parking_type",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    applies_sun_and_ph = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_offered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_free_parking_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_run",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    job_name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    file_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    file_mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    host_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    records_read = table.Column<int>(type: "INTEGER", nullable: false),
                    records_inserted = table.Column<int>(type: "INTEGER", nullable: false),
                    records_updated = table.Column<int>(type: "INTEGER", nullable: false),
                    records_unchanged = table.Column<int>(type: "INTEGER", nullable: false),
                    records_deactivated = table.Column<int>(type: "INTEGER", nullable: false),
                    records_rejected = table.Column<int>(type: "INTEGER", nullable: false),
                    attempt_number = table.Column<int>(type: "INTEGER", nullable: false),
                    error_summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "parking_system_type",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parking_system_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "short_term_parking_type",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    is_whole_day = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_available = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_short_term_parking_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_token",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    replaced_by_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_by_ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_token", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_token_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_run_error",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    job_run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    line_number = table.Column<int>(type: "INTEGER", nullable: false),
                    car_park_no = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    field_name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    raw_line = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_run_error", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_run_error_job_run_job_run_id",
                        column: x => x.job_run_id,
                        principalTable: "job_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "carpark",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    car_park_no = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    car_park_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    parking_system_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    short_term_parking_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    free_parking_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    has_night_parking = table.Column<bool>(type: "INTEGER", nullable: false),
                    deck_count = table.Column<int>(type: "INTEGER", nullable: false),
                    has_basement = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_row_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_modified_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_job_run_id = table.Column<int>(type: "INTEGER", nullable: true),
                    has_height_restriction = table.Column<bool>(type: "INTEGER", nullable: false),
                    gantry_height_m = table.Column<decimal>(type: "TEXT", precision: 4, scale: 2, nullable: true),
                    gantry_height_raw = table.Column<decimal>(type: "TEXT", precision: 4, scale: 2, nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: false),
                    longitude = table.Column<double>(type: "REAL", nullable: false),
                    svy21_x = table.Column<double>(type: "REAL", precision: 12, scale: 4, nullable: false),
                    svy21_y = table.Column<double>(type: "REAL", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carpark", x => x.id);
                    table.ForeignKey(
                        name: "FK_carpark_car_park_type_car_park_type_id",
                        column: x => x.car_park_type_id,
                        principalTable: "car_park_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_carpark_free_parking_type_free_parking_type_id",
                        column: x => x.free_parking_type_id,
                        principalTable: "free_parking_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_carpark_parking_system_type_parking_system_type_id",
                        column: x => x.parking_system_type_id,
                        principalTable: "parking_system_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_carpark_short_term_parking_type_short_term_parking_type_id",
                        column: x => x.short_term_parking_type_id,
                        principalTable: "short_term_parking_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_favourite",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    carpark_id = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_favourite", x => new { x.user_id, x.carpark_id });
                    table.ForeignKey(
                        name: "FK_user_favourite_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_favourite_carpark_carpark_id",
                        column: x => x.carpark_id,
                        principalTable: "carpark",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "car_park_type",
                columns: new[] { "id", "code", "is_active", "name" },
                values: new object[,]
                {
                    { 1, "SURFACE", true, "SURFACE CAR PARK" },
                    { 2, "MULTI_STOREY", true, "MULTI-STOREY CAR PARK" },
                    { 3, "BASEMENT", true, "BASEMENT CAR PARK" },
                    { 4, "SURFACE_MULTI_STOREY", true, "SURFACE/MULTI-STOREY CAR PARK" },
                    { 5, "COVERED", true, "COVERED CAR PARK" },
                    { 6, "MECHANISED_AND_SURFACE", true, "MECHANISED AND SURFACE CAR PARK" },
                    { 7, "MECHANISED", true, "MECHANISED CAR PARK" }
                });

            migrationBuilder.InsertData(
                table: "free_parking_type",
                columns: new[] { "id", "applies_sun_and_ph", "code", "description", "end_time", "is_offered", "start_time" },
                values: new object[,]
                {
                    { 1, false, "NONE", "NO", null, false, null },
                    { 2, true, "SUN_PH_0700_2230", "SUN & PH FR 7AM-10.30PM", new TimeOnly(22, 30, 0), true, new TimeOnly(7, 0, 0) },
                    { 3, true, "SUN_PH_1300_2230", "SUN & PH FR 1PM-10.30PM", new TimeOnly(22, 30, 0), true, new TimeOnly(13, 0, 0) }
                });

            migrationBuilder.InsertData(
                table: "parking_system_type",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { 1, "ELECTRONIC", "ELECTRONIC PARKING" },
                    { 2, "COUPON", "COUPON PARKING" }
                });

            migrationBuilder.InsertData(
                table: "short_term_parking_type",
                columns: new[] { "id", "code", "description", "end_time", "is_available", "is_whole_day", "start_time" },
                values: new object[,]
                {
                    { 1, "WHOLE_DAY", "WHOLE DAY", null, true, true, null },
                    { 2, "T0700_2230", "7AM-10.30PM", new TimeOnly(22, 30, 0), true, false, new TimeOnly(7, 0, 0) },
                    { 3, "NONE", "NO", null, false, false, null },
                    { 4, "T0700_1900", "7AM-7PM", new TimeOnly(19, 0, 0), true, false, new TimeOnly(7, 0, 0) }
                });

            migrationBuilder.CreateIndex(
                name: "ux_app_user_email",
                table: "app_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_car_park_type_code",
                table: "car_park_type",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carpark_car_park_type_id",
                table: "carpark",
                column: "car_park_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_carpark_free_parking_type_id",
                table: "carpark",
                column: "free_parking_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_carpark_parking_system_type_id",
                table: "carpark",
                column: "parking_system_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_carpark_short_term_parking_type_id",
                table: "carpark",
                column: "short_term_parking_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_carpark_keyset",
                table: "carpark",
                columns: new[] { "is_active", "car_park_no" });

            migrationBuilder.CreateIndex(
                name: "ux_carpark_car_park_no",
                table: "carpark",
                column: "car_park_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_free_parking_type_code",
                table: "free_parking_type",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_run_status",
                table: "job_run",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_job_run_file_hash",
                table: "job_run",
                column: "file_hash",
                unique: true,
                filter: "status = 'Succeeded'");

            migrationBuilder.CreateIndex(
                name: "ix_job_run_error_run",
                table: "job_run_error",
                columns: new[] { "job_run_id", "severity" });

            migrationBuilder.CreateIndex(
                name: "ux_parking_system_type_code",
                table: "parking_system_type",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_user",
                table: "refresh_token",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_token_hash",
                table: "refresh_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_short_term_parking_type_code",
                table: "short_term_parking_type",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_favourite_park",
                table: "user_favourite",
                column: "carpark_id");

            migrationBuilder.CreateIndex(
                name: "ix_favourite_user",
                table: "user_favourite",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            // ---------------------------------------------------------------------------------
            // Indexes over COMPLEX TYPE columns.
            //
            // EF Core 10 cannot declare an entity index across complex-type columns (HasIndex
            // resolves names against the entity's own properties), so these two are created here
            // in explicit SQL. See CarparkConfiguration for the note.
            // ---------------------------------------------------------------------------------

            // THE covering index for the three user-story filters.
            //
            // Column order is the whole design decision. A B-tree is usable for seeking only up to
            // and including the FIRST range predicate. gantry_height_m is the only range predicate,
            // so it must come LAST among the filters -- placing it earlier makes every column after
            // it dead weight. is_active leads because the soft-delete query filter applies it to
            // every query, and the trailing id makes the index COVERING for the keyset projection,
            // so SQLite answers from the index without touching the table.
            migrationBuilder.Sql("""
                CREATE INDEX ix_carpark_search ON carpark (
                    is_active,
                    has_night_parking,
                    free_parking_type_id,
                    has_height_restriction,
                    gantry_height_m,
                    id
                );
                """);

            // Bounding-box prefilter for radius search. The exact haversine pass runs over the
            // survivors -- a bounding box alone returns a square whose corners are 41% further
            // away than the radius the user asked for.
            migrationBuilder.Sql("""
                CREATE INDEX ix_carpark_geo ON carpark (latitude, longitude);
                """);

            // SQLite ignores foreign keys unless this is set PER CONNECTION. A schema full of
            // REFERENCES clauses that enforce nothing is worse than having no constraints at all,
            // because it produces false confidence. Also set by the connection interceptor; an
            // integration test asserts it is on.
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_carpark_search;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_carpark_geo;");

            migrationBuilder.DropTable(
                name: "job_run_error");

            migrationBuilder.DropTable(
                name: "refresh_token");

            migrationBuilder.DropTable(
                name: "user_favourite");

            migrationBuilder.DropTable(
                name: "job_run");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropTable(
                name: "carpark");

            migrationBuilder.DropTable(
                name: "car_park_type");

            migrationBuilder.DropTable(
                name: "free_parking_type");

            migrationBuilder.DropTable(
                name: "parking_system_type");

            migrationBuilder.DropTable(
                name: "short_term_parking_type");
        }
    }
}
