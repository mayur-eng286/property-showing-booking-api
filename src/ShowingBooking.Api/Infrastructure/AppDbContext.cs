using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ShowingBooking.Api.Domain;

namespace ShowingBooking.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Slot> Slots => Set<Slot>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingHistoryEntry> BookingHistory => Set<BookingHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite has no datetime type; make sure values read back are flagged as UTC.
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        modelBuilder.Entity<Slot>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.StartsAt).HasConversion(utc);
            e.Property(s => s.CreatedAt).HasConversion(utc);
            e.HasIndex(s => new { s.PropertyId, s.StartsAt });

            // Database-level safety net: even a bug in application code cannot overbook a slot.
            e.ToTable(t => t.HasCheckConstraint("CK_Slots_Seats", "\"BookedSeats\" >= 0 AND \"BookedSeats\" <= \"Capacity\""));
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).ValueGeneratedNever();
            e.Property(b => b.RenterEmail).HasMaxLength(254).IsRequired();
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.Version).IsConcurrencyToken();
            e.Property(b => b.CreatedAt).HasConversion(utc);

            e.HasOne(b => b.Slot).WithMany().HasForeignKey(b => b.SlotId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(b => b.History).WithOne().HasForeignKey(h => h.BookingId).OnDelete(DeleteBehavior.Cascade);

            // "A renter can hold only one active booking per slot" enforced by the database:
            // a partial unique index over Confirmed bookings only.
            e.HasIndex(b => new { b.SlotId, b.RenterEmail })
                .IsUnique()
                .HasFilter("\"Status\" = 'Confirmed'");
        });

        modelBuilder.Entity<BookingHistoryEntry>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.Actor).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.Reason).HasMaxLength(500);
            e.Property(h => h.OccurredAt).HasConversion(utc);
            e.HasIndex(h => h.BookingId);
        });
    }
}
