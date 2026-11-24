namespace ShowingBooking.Api.Domain;

public class Slot
{
    private Slot() { } // EF Core

    public Guid Id { get; private set; }
    public Guid PropertyId { get; private set; }
    public DateTime StartsAt { get; private set; }          // always UTC
    public int DurationMinutes { get; private set; }
    public int Capacity { get; private set; }

    /// <summary>
    /// Seats used by Confirmed bookings. Changed ONLY through atomic SQL updates in BookingService
    /// (never read-modify-write in C#), which is what prevents overbooking under concurrency.
    /// </summary>
    public int BookedSeats { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static Slot Create(Guid propertyId, DateTime startsAtUtc, int durationMinutes, int capacity, DateTime nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            StartsAt = startsAtUtc,
            DurationMinutes = durationMinutes,
            Capacity = capacity,
            BookedSeats = 0,
            CreatedAt = nowUtc
        };

    public bool HasStarted(DateTime nowUtc) => nowUtc >= StartsAt;
}
