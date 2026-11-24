using Microsoft.EntityFrameworkCore;
using ShowingBooking.Api.Domain;
using ShowingBooking.Api.Infrastructure;
using System.Net.Mail;

namespace ShowingBooking.Api.Application;

public class BookingService(AppDbContext db, TimeProvider clock)
{
    public const int MinPartySize = 1;
    public const int MaxPartySize = 6;
    public const int MaxReasonLength = 500;

    // ------------------------------------------------------------------ book
    public async Task<BookingResponse> BookAsync(Guid slotId, BookSlotRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var email = NormalizeEmail(request.RenterEmail, errors);
        if (request.PartySize is < MinPartySize or > MaxPartySize)
            errors.Add("partySize", $"partySize must be between {MinPartySize} and {MaxPartySize}.");
        errors.ThrowIfAny();

        var party = request.PartySize;
        var now = clock.GetUtcNow().UtcDateTime;

        var slot = await db.Slots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slotId, ct)
                   ?? throw new NotFoundException($"Slot {slotId} was not found.");

        // Domain rule: cannot book a slot that already started (throws DomainRuleException -> 409).
        var booking = Booking.Create(slot, email!, party, now);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1) Reserve seats ATOMICALLY. This is a single UPDATE with the capacity check in its WHERE clause,
        //    so two requests racing for the last seats cannot both succeed: the database serialises the
        //    writes and the loser updates 0 rows. It is deliberately the FIRST statement of the transaction
        //    (a write before any read) so SQLite takes the write lock immediately and waiters queue up
        //    instead of failing on a read->write lock upgrade.
        var reserved = await db.Slots
            .Where(s => s.Id == slotId && s.BookedSeats + party <= s.Capacity)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.BookedSeats, s => s.BookedSeats + party), ct);

        if (reserved == 0)
        {
            var remaining = await db.Slots.Where(s => s.Id == slotId)
                .Select(s => s.Capacity - s.BookedSeats).FirstAsync(ct);
            throw new ConflictException($"Not enough seats left: {remaining} remaining, {party} requested.");
        }

        // 2) One active booking per renter per slot (the partial unique index is the backstop).
        var alreadyBooked = await db.Bookings.AnyAsync(
            b => b.SlotId == slotId && b.RenterEmail == email && b.Status == BookingStatus.Confirmed, ct);
        if (alreadyBooked)
            throw new ConflictException($"{email} already has an active booking for this slot.");

        // 3) Insert booking + first history entry in the same transaction.
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException($"{email} already has an active booking for this slot.");
        }

        await tx.CommitAsync(ct);
        return ToResponse(booking);
    }

    // ------------------------------------------------------------ transition
    public async Task<BookingResponse> TransitionAsync(Guid bookingId, TransitionRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();

        if (!EnumParser.TryParse<BookingStatus>(request.To, out var to))
            errors.Add("to", "to must be one of: Confirmed, Cancelled, Attended, NoShow.");
        if (!EnumParser.TryParse<Actor>(request.Actor, out var actor))
            errors.Add("actor", "actor must be 'owner' or 'renter'.");

        var reason = request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
            errors.Add("reason", $"reason must be at most {MaxReasonLength} characters.");
        if (string.IsNullOrEmpty(reason))
            reason = null;
        errors.ThrowIfAny();

        var now = clock.GetUtcNow().UtcDateTime;

        var booking = await db.Bookings.Include(b => b.Slot).FirstOrDefaultAsync(b => b.Id == bookingId, ct)
                      ?? throw new NotFoundException($"Booking {bookingId} was not found.");

        // All transition rules (state, actor, timing) are enforced by the domain object.
        booking.Transition(to, actor, reason, now, booking.Slot.StartsAt);

        var party = booking.PartySize;
        var slotId = booking.SlotId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Writes the new status + history row. The UPDATE is guarded by the Version concurrency token,
            // so if another request changed this booking after we loaded it, 0 rows match and EF throws.
            await db.SaveChangesAsync(ct);

            if (to == BookingStatus.Cancelled)
            {
                // Cancelling frees the seats (atomic decrement, same transaction).
                await db.Slots
                    .Where(s => s.Id == slotId)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.BookedSeats, s => s.BookedSeats - party), ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The booking was changed by another request. Reload it and try again.");
        }

        return ToResponse(booking);
    }

    // ------------------------------------------------------------------ reads
    public async Task<BookingResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                      ?? throw new NotFoundException($"Booking {id} was not found.");
        return ToResponse(booking);
    }

    public async Task<IReadOnlyList<HistoryResponse>> GetHistoryAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Bookings.AnyAsync(b => b.Id == id, ct))
            throw new NotFoundException($"Booking {id} was not found.");

        var rows = await db.BookingHistory.AsNoTracking()
            .Where(h => h.BookingId == id)
            .OrderBy(h => h.Id)
            .ToListAsync(ct);

        return rows
            .Select(h => new HistoryResponse(h.FromStatus?.ToString(), h.ToStatus.ToString(), h.Actor.ToString(), h.Reason, h.OccurredAt))
            .ToList();
    }

    // ---------------------------------------------------------------- helpers
    private static string? NormalizeEmail(string? raw, ValidationErrors errors)
    {
        var email = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 254
            || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            errors.Add("renterEmail", "renterEmail must be a valid email address.");
            return null;
        }
        return email;
    }

    private static BookingResponse ToResponse(Booking b) =>
        new(b.Id, b.SlotId, b.RenterEmail, b.PartySize, b.Status.ToString(), b.CreatedAt);

    public async Task<IReadOnlyList<BookingResponse>> ListForSlotAsync(Guid slotId, string? status, CancellationToken ct)
    {
        BookingStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumParser.TryParse<BookingStatus>(status, out var parsed))
            {
                var errors = new ValidationErrors();
                errors.Add("status", "status must be one of: Confirmed, Cancelled, Attended, NoShow.");
                errors.ThrowIfAny();
            }
            filter = parsed;
        }

        if (!await db.Slots.AnyAsync(s => s.Id == slotId, ct))
            throw new NotFoundException($"Slot {slotId} was not found.");

        var query = db.Bookings.AsNoTracking().Where(b => b.SlotId == slotId);
        if (filter is { } f)
            query = query.Where(b => b.Status == f);

        var rows = await query.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<PagedResponse<BookingResponse>> ListAsync(
    Guid? slotId, string? renterEmail, string? status, int page, int pageSize, CancellationToken ct)
    {
        var errors = new ValidationErrors();

        BookingStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (EnumParser.TryParse<BookingStatus>(status, out var parsed))
                statusFilter = parsed;
            else
                errors.Add("status", "status must be one of: Confirmed, Cancelled, Attended, NoShow.");
        }
        if (page < 1)
            errors.Add("page", "page must be 1 or greater.");
        if (pageSize is < 1 or > SlotService.MaxPageSize)
            errors.Add("pageSize", $"pageSize must be between 1 and {SlotService.MaxPageSize}.");
        errors.ThrowIfAny();

        IQueryable<Booking> query = db.Bookings.AsNoTracking();

        if (slotId is { } sid)
            query = query.Where(b => b.SlotId == sid);
        if (!string.IsNullOrWhiteSpace(renterEmail))
        {
            var email = renterEmail.Trim().ToLowerInvariant();
            query = query.Where(b => b.RenterEmail == email);
        }
        if (statusFilter is { } st)
            query = query.Where(b => b.Status == st);

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id)   // newest first
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResponse<BookingResponse>(page, pageSize, total, rows.Select(ToResponse).ToList());
    }
}

/// <summary>Parses enum names case-insensitively and rejects numbers like "1".</summary>
public static class EnumParser
{
    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || int.TryParse(value, out _))
            return false;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result);
    }
}
