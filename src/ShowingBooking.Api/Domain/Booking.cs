namespace ShowingBooking.Api.Domain;

public class Booking
{
    private Booking() { } // EF Core

    public Guid Id { get; private set; }
    public Guid SlotId { get; private set; }
    public Slot Slot { get; private set; } = null!;
    public string RenterEmail { get; private set; } = string.Empty;   // stored lower-case
    public int PartySize { get; private set; }
    public BookingStatus Status { get; private set; }

    /// <summary>Optimistic concurrency token: two simultaneous transitions of one booking cannot both win.</summary>
    public int Version { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public ICollection<BookingHistoryEntry> History { get; private set; } = new List<BookingHistoryEntry>();

    /// <summary>Creates a Confirmed booking. Seat accounting is done by the caller (atomically in SQL).</summary>
    public static Booking Create(Slot slot, string renterEmail, int partySize, DateTime nowUtc)
    {
        if (slot.HasStarted(nowUtc))
            throw new DomainRuleException("This slot has already started and can no longer be booked.");

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            SlotId = slot.Id,
            RenterEmail = renterEmail,
            PartySize = partySize,
            Status = BookingStatus.Confirmed,
            CreatedAt = nowUtc
        };
        booking.History.Add(BookingHistoryEntry.Create(null, BookingStatus.Confirmed, Actor.Renter, "Booked", nowUtc));
        return booking;
    }

    /// <summary>
    /// The whole state machine lives here:
    ///   Confirmed -> Cancelled        (owner or renter, only BEFORE the slot starts)
    ///   Confirmed -> Attended/NoShow  (owner only, only AT/AFTER the slot start)
    ///   Cancelled / Attended / NoShow are final.
    /// </summary>
    public void Transition(BookingStatus to, Actor actor, string? reason, DateTime nowUtc, DateTime slotStartsAtUtc)
    {
        if (Status != BookingStatus.Confirmed)
            throw new DomainRuleException($"Booking is {Status}, which is a final state and cannot change.");

        var slotStarted = nowUtc >= slotStartsAtUtc;

        switch (to)
        {
            case BookingStatus.Cancelled:
                if (slotStarted)
                    throw new DomainRuleException("A booking can only be cancelled before the slot starts.");
                break;

            case BookingStatus.Attended:
            case BookingStatus.NoShow:
                if (actor != Actor.Owner)
                    throw new DomainRuleException($"Only the owner can mark a booking as {to}.");
                if (!slotStarted)
                    throw new DomainRuleException($"A booking can only be marked {to} after the slot starts.");
                break;

            default:
                throw new DomainRuleException($"Transition from {Status} to {to} is not allowed.");
        }

        var from = Status;
        Status = to;
        Version++;
        History.Add(BookingHistoryEntry.Create(from, to, actor, reason, nowUtc));
    }
}
