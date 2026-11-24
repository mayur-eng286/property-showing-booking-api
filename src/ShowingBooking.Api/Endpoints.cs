using Microsoft.AspNetCore.Mvc;
using ShowingBooking.Api.Application;

namespace ShowingBooking.Api;

public static class Endpoints
{
    public static void MapShowingApi(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------- slots
        app.MapPost("/slots", async (CreateSlotRequest request, SlotService slots, CancellationToken ct) =>
            {
                var slot = await slots.CreateAsync(request, ct);
                return Results.Created($"/slots/{slot.Id}", slot);
            })
            .WithTags("Slots")
            .WithSummary("Create a showing slot (owner)");

        app.MapGet("/slots", async (
                Guid? propertyId,
                [FromQuery(Name = "from")] DateTimeOffset? fromTime,
                [FromQuery(Name = "to")] DateTimeOffset? toTime,
                SlotService slots,
                CancellationToken ct,
                int page = 1,
                int pageSize = 20) =>
                Results.Ok(await slots.ListAsync(propertyId, fromTime, toTime, page, pageSize, ct)))
            .WithTags("Slots")
            .WithSummary("List slots ordered by start time, with seats remaining (maxPageSize 100)");

        app.MapGet("/slots/{id}", async (Guid id, SlotService slots, CancellationToken ct) =>
                Results.Ok(await slots.GetAsync(id, ct)))
            .WithTags("Slots")
            .WithSummary("Get one slot");

        // ---------------------------------------------------------- bookings
        app.MapPost("/slots/{id}/bookings", async (Guid id, BookSlotRequest request, BookingService bookings, CancellationToken ct) =>
            {
                var booking = await bookings.BookAsync(id, request, ct);
                return Results.Created($"/bookings/{booking.Id}", booking);
            })
            .WithTags("Bookings")
            .WithSummary("Book a slot for a party (renter)");

        app.MapGet("/bookings/{id}", async (Guid id, BookingService bookings, CancellationToken ct) =>
                Results.Ok(await bookings.GetAsync(id, ct)))
            .WithTags("Bookings")
            .WithSummary("Get one booking");

        app.MapPost("/bookings/{id}/transitions", async (Guid id, TransitionRequest request, BookingService bookings, CancellationToken ct) =>
                Results.Ok(await bookings.TransitionAsync(id, request, ct)))
            .WithTags("Bookings")
            .WithSummary("Cancel, or mark Attended / NoShow. Body: { \"to\": \"Cancelled\", \"reason\": \"...\", \"actor\": \"owner\" | \"renter\" }");

        app.MapGet("/bookings/{id}/history", async (Guid id, BookingService bookings, CancellationToken ct) =>
                Results.Ok(await bookings.GetHistoryAsync(id, ct)))
            .WithTags("Bookings")
            .WithSummary("Every state change with timestamp, actor and reason");

        app.MapGet("/slots/{id}/bookings", async (Guid id, string? status, BookingService bookings, CancellationToken ct) =>
        Results.Ok(await bookings.ListForSlotAsync(id, status, ct)))
    .WithTags("Bookings")
    .WithSummary("List bookings of a slot, oldest first (optional ?status=Confirmed)");

        app.MapGet("/bookings", async (
            Guid? slotId,
            string? renterEmail,
            string? status,
            BookingService bookings,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
            Results.Ok(await bookings.ListAsync(slotId, renterEmail, status, page, pageSize, ct)))
        .WithTags("Bookings")
        .WithSummary("List all bookings, newest first. Optional filters: slotId, renterEmail, status (maxPageSize 100)");
    }
}
