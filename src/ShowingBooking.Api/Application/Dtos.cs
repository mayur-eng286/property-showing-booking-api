namespace ShowingBooking.Api.Application;

// ---- requests ----
public record CreateSlotRequest(Guid PropertyId, DateTimeOffset StartsAt, int DurationMinutes, int Capacity);

public record BookSlotRequest(string? RenterEmail, int PartySize);

/// <summary>To = Cancelled | Attended | NoShow; Actor = owner | renter (case-insensitive).</summary>
public record TransitionRequest(string? To, string? Reason, string? Actor);

// ---- responses (enums are returned as strings) ----
public record SlotResponse(
    Guid Id,
    Guid PropertyId,
    DateTime StartsAt,
    int DurationMinutes,
    int Capacity,
    int BookedSeats,
    int SeatsRemaining);

public record BookingResponse(
    Guid Id,
    Guid SlotId,
    string RenterEmail,
    int PartySize,
    string Status,
    DateTime CreatedAt);

public record HistoryResponse(string? From, string To, string Actor, string? Reason, DateTime Timestamp);

public record PagedResponse<T>(int Page, int PageSize, int TotalCount, IReadOnlyList<T> Items);
