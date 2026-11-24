using Microsoft.EntityFrameworkCore;
using ShowingBooking.Api.Domain;
using ShowingBooking.Api.Infrastructure;

namespace ShowingBooking.Api.Application;

public class SlotService(AppDbContext db, TimeProvider clock)
{
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 120;
    public const int MinCapacity = 1;
    public const int MaxCapacity = 20;
    public const int MaxPageSize = 100;

    public async Task<SlotResponse> CreateAsync(CreateSlotRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var errors = new ValidationErrors();

        if (request.PropertyId == Guid.Empty)
            errors.Add("propertyId", "propertyId is required.");
        if (request.StartsAt.UtcDateTime <= now)
            errors.Add("startsAt", "startsAt must be in the future.");
        if (request.DurationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            errors.Add("durationMinutes", $"durationMinutes must be between {MinDurationMinutes} and {MaxDurationMinutes}.");
        if (request.Capacity is < MinCapacity or > MaxCapacity)
            errors.Add("capacity", $"capacity must be between {MinCapacity} and {MaxCapacity}.");
        errors.ThrowIfAny();

        var slot = Slot.Create(request.PropertyId, request.StartsAt.UtcDateTime, request.DurationMinutes, request.Capacity, now);
        db.Slots.Add(slot);
        await db.SaveChangesAsync(ct);
        return ToResponse(slot);
    }

    public async Task<SlotResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var slot = await db.Slots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
                   ?? throw new NotFoundException($"Slot {id} was not found.");
        return ToResponse(slot);
    }

    public async Task<PagedResponse<SlotResponse>> ListAsync(
        Guid? propertyId, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (page < 1)
            errors.Add("page", "page must be 1 or greater.");
        if (pageSize is < 1 or > MaxPageSize)
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        if (from is not null && to is not null && from > to)
            errors.Add("from", "from must not be later than to.");
        errors.ThrowIfAny();

        IQueryable<Slot> query = db.Slots.AsNoTracking();

        if (propertyId is { } pid)
            query = query.Where(s => s.PropertyId == pid);
        if (from is { } f)
        {
            var fromUtc = f.UtcDateTime;
            query = query.Where(s => s.StartsAt >= fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = t.UtcDateTime;
            query = query.Where(s => s.StartsAt <= toUtc);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SlotResponse(
                s.Id, s.PropertyId, s.StartsAt, s.DurationMinutes, s.Capacity, s.BookedSeats, s.Capacity - s.BookedSeats))
            .ToListAsync(ct);

        return new PagedResponse<SlotResponse>(page, pageSize, total, items);
    }

    private static SlotResponse ToResponse(Slot s) =>
        new(s.Id, s.PropertyId, s.StartsAt, s.DurationMinutes, s.Capacity, s.BookedSeats, s.Capacity - s.BookedSeats);
}
