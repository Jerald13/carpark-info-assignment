using CarparkInfo.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarparkInfo.Infrastructure.Persistence.Configurations;

/// <summary>Maps user accounts.</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("app_user");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(512).IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
        builder.Property(u => u.Role).HasColumnName("role").HasMaxLength(20).IsRequired();
        builder.Property(u => u.FailedLoginCount).HasColumnName("failed_login_count");
        builder.Property(u => u.LockoutEndsAt).HasColumnName("lockout_ends_at");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ux_app_user_email");

        builder.HasMany(u => u.Favourites)
            .WithOne(f => f.User)
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps the user/carpark junction.
/// </summary>
/// <remarks>
/// Three decisions worth noting. The composite primary key makes a duplicate favourite
/// structurally impossible, so the idempotent <c>PUT</c> is guaranteed by the schema rather than by
/// remembering to check first. Cascade on user means account deletion removes favourites, which a
/// GDPR-style erasure request gets for free. Restrict on carpark means a favourited carpark
/// <b>cannot</b> be hard-deleted -- which is precisely why ingestion soft-deactivates instead.
/// </remarks>
internal sealed class FavouriteConfiguration : IEntityTypeConfiguration<Favourite>
{
    public void Configure(EntityTypeBuilder<Favourite> builder)
    {
        builder.ToTable("user_favourite");

        builder.HasKey(f => new { f.UserId, f.CarparkId });
        builder.Property(f => f.UserId).HasColumnName("user_id");
        builder.Property(f => f.CarparkId).HasColumnName("carpark_id");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");

        builder.HasOne(f => f.Carpark)
            .WithMany()
            .HasForeignKey(f => f.CarparkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => new { f.UserId, f.CreatedAt })
            .HasDatabaseName("ix_favourite_user")
            .IsDescending(false, true);

        builder.HasIndex(f => f.CarparkId).HasDatabaseName("ix_favourite_park");
    }
}

/// <summary>Maps refresh tokens. The raw token is never stored, only its hash.</summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_token");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.RevokedAt).HasColumnName("revoked_at");
        builder.Property(t => t.ReplacedById).HasColumnName("replaced_by_id");
        builder.Property(t => t.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);

        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_token_hash");
        builder.HasIndex(t => t.UserId).HasDatabaseName("ix_refresh_token_user");

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
