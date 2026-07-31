using CarparkInfo.Domain.Carparks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarparkInfo.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the carpark aggregate, its value objects and the indexes that make the user-story filters
/// index-seekable.
/// </summary>
internal sealed class CarparkConfiguration : IEntityTypeConfiguration<Carpark>
{
    public void Configure(EntityTypeBuilder<Carpark> builder)
    {
        builder.ToTable("carpark");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");

        builder.Property(c => c.CarParkNo)
            .HasColumnName("car_park_no")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(c => c.Address)
            .HasColumnName("address")
            .HasMaxLength(200)
            .IsRequired();

        // Location and HeightRestriction are EF Core complex types: value semantics, no identity,
        // mapped to columns in this same table rather than joined from another one.
        builder.ComplexProperty(c => c.Location, location =>
        {
            location.Property(l => l.Svy21X).HasColumnName("svy21_x").HasPrecision(12, 4);
            location.Property(l => l.Svy21Y).HasColumnName("svy21_y").HasPrecision(12, 4);
            location.Property(l => l.Latitude).HasColumnName("latitude");
            location.Property(l => l.Longitude).HasColumnName("longitude");
        });

        builder.ComplexProperty(c => c.HeightRestriction, height =>
        {
            // SQLite's dynamic typing will happily store a REAL where a DECIMAL was intended and
            // lose exactness. Heights are compared for equality, and 2.15 must not become
            // 2.1499999, so precision is declared explicitly.
            height.Property(h => h.MaximumVehicleHeightMetres)
                .HasColumnName("gantry_height_m")
                .HasPrecision(4, 2);

            height.Property(h => h.RawSourceValue)
                .HasColumnName("gantry_height_raw")
                .HasPrecision(4, 2)
                .IsRequired();

            height.Property(h => h.IsRestricted)
                .HasColumnName("has_height_restriction");
        });

        builder.Property(c => c.HasNightParking).HasColumnName("has_night_parking");
        builder.Property(c => c.DeckCount).HasColumnName("deck_count");
        builder.Property(c => c.HasBasement).HasColumnName("has_basement");

        builder.Property(c => c.SourceRowHash)
            .HasColumnName("source_row_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(c => c.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(c => c.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(c => c.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(c => c.LastModifiedAt).HasColumnName("last_modified_at");
        builder.Property(c => c.LastJobRunId).HasColumnName("last_job_run_id");

        builder.Property(c => c.CarParkTypeId).HasColumnName("car_park_type_id");
        builder.Property(c => c.ParkingSystemTypeId).HasColumnName("parking_system_type_id");
        builder.Property(c => c.ShortTermParkingTypeId).HasColumnName("short_term_parking_type_id");
        builder.Property(c => c.FreeParkingTypeId).HasColumnName("free_parking_type_id");

        // Reference data is never cascade-deleted out from under a carpark.
        builder.HasOne(c => c.CarParkType).WithMany()
            .HasForeignKey(c => c.CarParkTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.ParkingSystemType).WithMany()
            .HasForeignKey(c => c.ParkingSystemTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.ShortTermParkingType).WithMany()
            .HasForeignKey(c => c.ShortTermParkingTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.FreeParkingType).WithMany()
            .HasForeignKey(c => c.FreeParkingTypeId).OnDelete(DeleteBehavior.Restrict);

        // Business key.
        builder.HasIndex(c => c.CarParkNo)
            .IsUnique()
            .HasDatabaseName("ux_carpark_car_park_no");

        // Keyset pagination ordering.
        builder.HasIndex(c => new { c.IsActive, c.CarParkNo })
            .HasDatabaseName("ix_carpark_keyset");

        // NOTE: ix_carpark_search (the covering index for the three user-story filters) and
        // ix_carpark_geo (the radius-search prefilter) span columns belonging to the Location and
        // HeightRestriction COMPLEX TYPES. EF Core 10 cannot declare an entity index over
        // complex-type columns - HasIndex resolves names against the entity's own properties and
        // would create a shadow property instead.
        //
        // Both are therefore created as explicit SQL in the InitialSchema migration, where their
        // column order is documented. Queries still translate normally; only the index DECLARATION
        // has to live outside the model. An integration test asserts via EXPLAIN QUERY PLAN that
        // the search query actually uses the covering index, so the arrangement is verified rather
        // than assumed.

        // EF Core 10 named query filter: the admin and audit paths can disable exactly this one.
        builder.HasQueryFilter(CarparkDbContext.SoftDeleteFilter, c => c.IsActive);
    }
}
