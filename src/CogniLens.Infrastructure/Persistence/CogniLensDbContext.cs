using CogniLens.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CogniLens.Infrastructure.Persistence;

public class CogniLensDbContext(DbContextOptions<CogniLensDbContext> options) : DbContext(options)
{
    public DbSet<Call> Calls => Set<Call>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<QaReport> QaReports => Set<QaReport>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<JobStatus> JobStatuses => Set<JobStatus>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Call>(entity =>
        {
            entity.Property(c => c.BlobUri).HasMaxLength(1024);
            entity.Property(c => c.OriginalFileName).HasMaxLength(260);
            entity.HasIndex(c => c.Status);
        });

        modelBuilder.Entity<TranscriptSegment>(entity =>
        {
            entity.HasOne(t => t.Call)
                .WithMany(c => c.TranscriptSegments)
                .HasForeignKey(t => t.CallId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(t => new { t.CallId, t.SequenceNumber });
        });

        modelBuilder.Entity<QaReport>(entity =>
        {
            entity.HasOne(q => q.Call)
                .WithOne(c => c.QaReport)
                .HasForeignKey<QaReport>(q => q.CallId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(q => q.CallId).IsUnique();
        });

        modelBuilder.Entity<Rubric>(entity =>
        {
            entity.Property(r => r.Name).HasMaxLength(200);
            entity.Property(r => r.Category).HasMaxLength(100);
            entity.HasData(RubricSeedData.All);
        });

        modelBuilder.Entity<JobStatus>(entity =>
        {
            entity.HasOne(j => j.Call)
                .WithMany(c => c.JobStatuses)
                .HasForeignKey(j => j.CallId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(j => j.MessageId).IsUnique();
        });
    }
}
