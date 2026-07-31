using CarparkInfo.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarparkInfo.Infrastructure.Persistence.Configurations;

/// <summary>Maps ingestion run history.</summary>
internal sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.ToTable("job_run");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.JobName).HasColumnName("job_name").HasMaxLength(80).IsRequired();
        builder.Property(r => r.FileName).HasColumnName("file_name").HasMaxLength(260).IsRequired();
        builder.Property(r => r.FileHash).HasColumnName("file_hash").HasMaxLength(64).IsRequired();

        // Enums stored as text: a status column that reads "Succeeded" rather than "2" is worth
        // the handful of bytes to anyone reading the table during an incident.
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.FileMode).HasColumnName("file_mode").HasConversion<string>().HasMaxLength(20);

        builder.Property(r => r.StartedAt).HasColumnName("started_at");
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");
        builder.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(r => r.HostName).HasColumnName("host_name").HasMaxLength(128);
        builder.Property(r => r.RecordsRead).HasColumnName("records_read");
        builder.Property(r => r.RecordsInserted).HasColumnName("records_inserted");
        builder.Property(r => r.RecordsUpdated).HasColumnName("records_updated");
        builder.Property(r => r.RecordsUnchanged).HasColumnName("records_unchanged");
        builder.Property(r => r.RecordsDeactivated).HasColumnName("records_deactivated");
        builder.Property(r => r.RecordsRejected).HasColumnName("records_rejected");
        builder.Property(r => r.AttemptNumber).HasColumnName("attempt_number");
        builder.Property(r => r.ErrorSummary).HasColumnName("error_summary").HasMaxLength(2000);

        // Idempotency: at most ONE successful run may exist per file hash. A filtered unique index
        // enforces it in the database, so a race between two hosts cannot produce two successes.
        builder.HasIndex(r => r.FileHash)
            .IsUnique()
            .HasFilter("status = 'Succeeded'")
            .HasDatabaseName("ux_job_run_file_hash");

        // Startup reclaim scans for Running rows whose lease has lapsed.
        builder.HasIndex(r => new { r.Status, r.LeaseExpiresAt })
            .HasDatabaseName("ix_job_run_status");

        builder.HasMany(r => r.Errors)
            .WithOne(e => e.JobRun)
            .HasForeignKey(e => e.JobRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(JobRun.Errors))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Maps the defect report produced by a run.</summary>
internal sealed class JobRunErrorConfiguration : IEntityTypeConfiguration<JobRunError>
{
    public void Configure(EntityTypeBuilder<JobRunError> builder)
    {
        builder.ToTable("job_run_error");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.JobRunId).HasColumnName("job_run_id");
        builder.Property(e => e.LineNumber).HasColumnName("line_number");
        builder.Property(e => e.CarParkNo).HasColumnName("car_park_no").HasMaxLength(10);
        builder.Property(e => e.FieldName).HasColumnName("field_name").HasMaxLength(60);
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(60).IsRequired();
        builder.Property(e => e.Severity).HasColumnName("severity").HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Message).HasColumnName("message").HasMaxLength(1000).IsRequired();
        builder.Property(e => e.RawLine).HasColumnName("raw_line").HasMaxLength(2000);

        builder.HasIndex(e => new { e.JobRunId, e.Severity }).HasDatabaseName("ix_job_run_error_run");
    }
}
