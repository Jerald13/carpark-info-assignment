using CarparkInfo.Domain.Carparks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarparkInfo.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps and seeds the carpark type lookup. Seven values, measured from the source file.
/// </summary>
/// <remarks>
/// Seeded via <c>HasData</c> with fixed ids so they are identical across environments. Runtime
/// seeding would produce different ids per database, which breaks any test fixture or migration
/// that references them.
/// </remarks>
internal sealed class CarParkTypeConfiguration : IEntityTypeConfiguration<CarParkType>
{
    public void Configure(EntityTypeBuilder<CarParkType> builder)
    {
        builder.ToTable("car_park_type");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.Property(t => t.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ux_car_park_type_code");

        // Row counts from hdb-carpark-information-20220824010400.csv.
        builder.HasData(
            Seed(1, "SURFACE", "SURFACE CAR PARK"),                                   // 1,087
            Seed(2, "MULTI_STOREY", "MULTI-STOREY CAR PARK"),                          // 1,033
            Seed(3, "BASEMENT", "BASEMENT CAR PARK"),                                  //    38
            Seed(4, "SURFACE_MULTI_STOREY", "SURFACE/MULTI-STOREY CAR PARK"),          //    12
            Seed(5, "COVERED", "COVERED CAR PARK"),                                    //     8
            Seed(6, "MECHANISED_AND_SURFACE", "MECHANISED AND SURFACE CAR PARK"),      //     2
            Seed(7, "MECHANISED", "MECHANISED CAR PARK"));                             //     1
    }

    private static object Seed(int id, string code, string name) =>
        new { Id = id, Code = code, Name = name, IsActive = true };
}

/// <summary>Maps and seeds the parking system lookup. Two values.</summary>
internal sealed class ParkingSystemTypeConfiguration : IEntityTypeConfiguration<ParkingSystemType>
{
    public void Configure(EntityTypeBuilder<ParkingSystemType> builder)
    {
        builder.ToTable("parking_system_type");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ux_parking_system_type_code");

        builder.HasData(
            new { Id = 1, Code = "ELECTRONIC", Name = "ELECTRONIC PARKING" },   // 1,998
            new { Id = 2, Code = "COUPON", Name = "COUPON PARKING" });          //   183
    }
}

/// <summary>Maps and seeds the short-term parking lookup. Four values.</summary>
internal sealed class ShortTermParkingTypeConfiguration : IEntityTypeConfiguration<ShortTermParkingType>
{
    public void Configure(EntityTypeBuilder<ShortTermParkingType> builder)
    {
        builder.ToTable("short_term_parking_type");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").HasMaxLength(80).IsRequired();
        builder.Property(t => t.StartTime).HasColumnName("start_time");
        builder.Property(t => t.EndTime).HasColumnName("end_time");
        builder.Property(t => t.IsWholeDay).HasColumnName("is_whole_day");
        builder.Property(t => t.IsAvailable).HasColumnName("is_available");
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ux_short_term_parking_type_code");

        builder.HasData(
            new
            {
                Id = 1, Code = "WHOLE_DAY", Description = "WHOLE DAY",
                StartTime = (TimeOnly?)null, EndTime = (TimeOnly?)null,
                IsWholeDay = true, IsAvailable = true,
            },                                                                          // 1,758
            new
            {
                Id = 2, Code = "T0700_2230", Description = "7AM-10.30PM",
                StartTime = (TimeOnly?)new TimeOnly(7, 0), EndTime = (TimeOnly?)new TimeOnly(22, 30),
                IsWholeDay = false, IsAvailable = true,
            },                                                                          //   261
            new
            {
                Id = 3, Code = "NONE", Description = "NO",
                StartTime = (TimeOnly?)null, EndTime = (TimeOnly?)null,
                IsWholeDay = false, IsAvailable = false,
            },                                                                          //   119
            new
            {
                Id = 4, Code = "T0700_1900", Description = "7AM-7PM",
                StartTime = (TimeOnly?)new TimeOnly(7, 0), EndTime = (TimeOnly?)new TimeOnly(19, 0),
                IsWholeDay = false, IsAvailable = true,
            });                                                                         //    43
    }
}

/// <summary>
/// Maps and seeds the free parking lookup. Three values.
/// </summary>
/// <remarks>
/// Note what is <i>not</i> here: a <c>YES</c> value. The source has none. "Offers free parking"
/// is <see cref="FreeParkingType.IsOffered"/>, which is false only for <c>NONE</c>.
/// </remarks>
internal sealed class FreeParkingTypeConfiguration : IEntityTypeConfiguration<FreeParkingType>
{
    public void Configure(EntityTypeBuilder<FreeParkingType> builder)
    {
        builder.ToTable("free_parking_type");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").HasMaxLength(80).IsRequired();
        builder.Property(t => t.StartTime).HasColumnName("start_time");
        builder.Property(t => t.EndTime).HasColumnName("end_time");
        builder.Property(t => t.AppliesOnSundaysAndPublicHolidays).HasColumnName("applies_sun_and_ph");
        builder.Property(t => t.IsOffered).HasColumnName("is_offered");
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ux_free_parking_type_code");

        builder.HasData(
            new
            {
                Id = 1, Code = "NONE", Description = "NO",
                StartTime = (TimeOnly?)null, EndTime = (TimeOnly?)null,
                AppliesOnSundaysAndPublicHolidays = false, IsOffered = false,
            },                                                                          //   576
            new
            {
                Id = 2, Code = "SUN_PH_0700_2230", Description = "SUN & PH FR 7AM-10.30PM",
                StartTime = (TimeOnly?)new TimeOnly(7, 0), EndTime = (TimeOnly?)new TimeOnly(22, 30),
                AppliesOnSundaysAndPublicHolidays = true, IsOffered = true,
            },                                                                          // 1,594
            new
            {
                Id = 3, Code = "SUN_PH_1300_2230", Description = "SUN & PH FR 1PM-10.30PM",
                StartTime = (TimeOnly?)new TimeOnly(13, 0), EndTime = (TimeOnly?)new TimeOnly(22, 30),
                AppliesOnSundaysAndPublicHolidays = true, IsOffered = true,
            });                                                                         //    11
    }
}
