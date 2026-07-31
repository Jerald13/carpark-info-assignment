using CarparkInfo.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarparkInfo.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the staging table.
/// </summary>
/// <remarks>
/// Columns are flat primitives rather than the complex types used on <c>carpark</c>. Staging is a
/// transport buffer, not a domain model - the merge reads it with set-based SQL, and flat columns
/// keep that statement readable.
/// </remarks>
internal sealed class CarparkStagingRowConfiguration : IEntityTypeConfiguration<CarparkStagingRow>
{
    public void Configure(EntityTypeBuilder<CarparkStagingRow> builder)
    {
        builder.ToTable("carpark_staging");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.JobRunId).HasColumnName("job_run_id");
        builder.Property(s => s.CarParkNo).HasColumnName("car_park_no").HasMaxLength(10).IsRequired();
        builder.Property(s => s.Address).HasColumnName("address").HasMaxLength(200).IsRequired();
        builder.Property(s => s.Svy21X).HasColumnName("svy21_x").HasPrecision(12, 4);
        builder.Property(s => s.Svy21Y).HasColumnName("svy21_y").HasPrecision(12, 4);
        builder.Property(s => s.Latitude).HasColumnName("latitude");
        builder.Property(s => s.Longitude).HasColumnName("longitude");
        builder.Property(s => s.CarParkTypeId).HasColumnName("car_park_type_id");
        builder.Property(s => s.ParkingSystemTypeId).HasColumnName("parking_system_type_id");
        builder.Property(s => s.ShortTermParkingTypeId).HasColumnName("short_term_parking_type_id");
        builder.Property(s => s.FreeParkingTypeId).HasColumnName("free_parking_type_id");
        builder.Property(s => s.HasNightParking).HasColumnName("has_night_parking");
        builder.Property(s => s.DeckCount).HasColumnName("deck_count");
        builder.Property(s => s.GantryHeightMetres).HasColumnName("gantry_height_m").HasPrecision(4, 2);
        builder.Property(s => s.HasHeightRestriction).HasColumnName("has_height_restriction");
        builder.Property(s => s.GantryHeightRaw).HasColumnName("gantry_height_raw").HasPrecision(4, 2);
        builder.Property(s => s.HasBasement).HasColumnName("has_basement");
        builder.Property(s => s.SourceRowHash).HasColumnName("source_row_hash").HasMaxLength(64).IsRequired();
        builder.Property(s => s.LineNumber).HasColumnName("line_number");

        // The merge probes staging by (run, key).
        builder.HasIndex(s => new { s.JobRunId, s.CarParkNo })
            .HasDatabaseName("ix_staging_run_carpark");
    }
}
