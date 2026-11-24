namespace ShowingBooking.Api.Domain;

public class BookingHistoryEntry
{
    private BookingHistoryEntry() { } // EF Core

    public int Id { get; private set; }                 // identity: gives a stable ordering
    public Guid BookingId { get; private set; }
    public BookingStatus? FromStatus { get; private set; }   // null for the initial "Booked" entry
    public BookingStatus ToStatus { get; private set; }
    public Actor Actor { get; private set; }
    public string? Reason { get; private set; }
    public DateTime OccurredAt { get; private set; }

    internal static BookingHistoryEntry Create(BookingStatus? from, BookingStatus to, Actor actor, string? reason, DateTime nowUtc) =>
        new() { FromStatus = from, ToStatus = to, Actor = actor, Reason = reason, OccurredAt = nowUtc };
}
