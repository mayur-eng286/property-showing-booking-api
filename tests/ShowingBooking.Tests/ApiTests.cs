using ShowingBooking.Api.Application;
using System.Net;
using System.Net.Http.Json;

namespace ShowingBooking.Tests;

/// <summary>End-to-end tests through the real HTTP pipeline, EF Core and SQLite.</summary>
public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _http;

    public ApiTests(ApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    // ------------------------------------------------------------ helpers
    private DateTimeOffset Now => _factory.Clock.GetUtcNow();

    private async Task<SlotResponse> CreateSlot(int capacity = 6, int minutesFromNow = 60, Guid? propertyId = null)
    {
        var response = await _http.PostAsJsonAsync("/slots",
            new CreateSlotRequest(propertyId ?? Guid.NewGuid(), Now.AddMinutes(minutesFromNow), 30, capacity));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SlotResponse>())!;
    }

    private Task<HttpResponseMessage> Book(Guid slotId, string email, int party = 1) =>
        _http.PostAsJsonAsync($"/slots/{slotId}/bookings", new BookSlotRequest(email, party));

    private async Task<BookingResponse> BookOk(Guid slotId, string email, int party = 1)
    {
        var response = await Book(slotId, email, party);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookingResponse>())!;
    }

    private Task<HttpResponseMessage> Transition(Guid bookingId, string to, string actor, string? reason = null) =>
        _http.PostAsJsonAsync($"/bookings/{bookingId}/transitions", new TransitionRequest(to, reason, actor));

    private async Task<SlotResponse> GetSlot(Guid id) =>
        (await _http.GetFromJsonAsync<SlotResponse>($"/slots/{id}"))!;

    // ------------------------------------------------------- POST /slots
    [Fact]
    public async Task Create_slot_returns_201_with_full_capacity_available()
    {
        var slot = await CreateSlot(capacity: 8);

        Assert.Equal(8, slot.Capacity);
        Assert.Equal(0, slot.BookedSeats);
        Assert.Equal(8, slot.SeatsRemaining);
    }

    [Theory]
    [InlineData(14, 5)]
    [InlineData(121, 5)]
    [InlineData(30, 0)]
    [InlineData(30, 21)]
    public async Task Create_slot_rejects_out_of_range_values_with_400(int duration, int capacity)
    {
        var response = await _http.PostAsJsonAsync("/slots",
            new CreateSlotRequest(Guid.NewGuid(), Now.AddHours(2), duration, capacity));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_slot_in_the_past_returns_400()
    {
        var response = await _http.PostAsJsonAsync("/slots",
            new CreateSlotRequest(Guid.NewGuid(), Now.AddMinutes(-1), 30, 5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_slot_without_property_id_returns_400()
    {
        var response = await _http.PostAsJsonAsync("/slots",
            new CreateSlotRequest(Guid.Empty, Now.AddHours(1), 30, 5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------- GET /slots
    [Fact]
    public async Task List_slots_is_filtered_ordered_paged_and_shows_seats_remaining()
    {
        var property = Guid.NewGuid();
        var late = await CreateSlot(capacity: 4, minutesFromNow: 300, propertyId: property);
        var early = await CreateSlot(capacity: 4, minutesFromNow: 100, propertyId: property);
        var middle = await CreateSlot(capacity: 4, minutesFromNow: 200, propertyId: property);
        await CreateSlot(propertyId: Guid.NewGuid()); // other property: must not appear
        await BookOk(early.Id, "a@example.com", party: 3);

        var all = await _http.GetFromJsonAsync<PagedResponse<SlotResponse>>($"/slots?propertyId={property}");

        Assert.Equal(3, all!.TotalCount);
        Assert.Equal(new[] { early.Id, middle.Id, late.Id }, all.Items.Select(s => s.Id).ToArray());
        Assert.Equal(1, all.Items[0].SeatsRemaining);

        var secondPage = await _http.GetFromJsonAsync<PagedResponse<SlotResponse>>(
            $"/slots?propertyId={property}&page=2&pageSize=2");
        Assert.Equal(3, secondPage!.TotalCount);
        Assert.Equal(late.Id, Assert.Single(secondPage.Items).Id);

        var from = Uri.EscapeDataString(middle.StartsAt.ToString("o"));
        var filtered = await _http.GetFromJsonAsync<PagedResponse<SlotResponse>>(
            $"/slots?propertyId={property}&from={from}");
        Assert.Equal(new[] { middle.Id, late.Id }, filtered!.Items.Select(s => s.Id).ToArray());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_slots_rejects_bad_paging_with_400(string query)
    {
        var response = await _http.GetAsync($"/slots?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------ POST /slots/{id}/bookings
    [Fact]
    public async Task Booking_starts_confirmed_and_uses_seats()
    {
        var slot = await CreateSlot(capacity: 6);

        var booking = await BookOk(slot.Id, "renter@example.com", party: 4);

        Assert.Equal("Confirmed", booking.Status);
        Assert.Equal(2, (await GetSlot(slot.Id)).SeatsRemaining);
    }

    [Theory]
    [InlineData("not-an-email", 2)]
    [InlineData("", 2)]
    [InlineData("a@example.com", 0)]
    [InlineData("a@example.com", 7)]
    public async Task Booking_rejects_bad_input_with_400(string email, int party)
    {
        var slot = await CreateSlot();

        var response = await Book(slot.Id, email, party);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Booking_an_unknown_slot_returns_404()
    {
        var response = await Book(Guid.NewGuid(), "a@example.com");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Booking_more_seats_than_remain_returns_409_and_changes_nothing()
    {
        var slot = await CreateSlot(capacity: 5);
        await BookOk(slot.Id, "a@example.com", party: 3);

        var tooMany = await Book(slot.Id, "b@example.com", party: 3);
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Equal(2, (await GetSlot(slot.Id)).SeatsRemaining);

        await BookOk(slot.Id, "c@example.com", party: 2); // exactly fills the slot
        Assert.Equal(0, (await GetSlot(slot.Id)).SeatsRemaining);
    }

    [Fact]
    public async Task Same_renter_cannot_hold_two_active_bookings_for_one_slot()
    {
        var slot = await CreateSlot();
        await BookOk(slot.Id, "dup@example.com");

        var second = await Book(slot.Id, "DUP@Example.com "); // same address after normalisation

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(5, (await GetSlot(slot.Id)).SeatsRemaining); // the failed attempt did not keep seats
    }

    [Fact]
    public async Task Renter_can_book_again_after_cancelling()
    {
        var slot = await CreateSlot();
        var first = await BookOk(slot.Id, "again@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Transition(first.Id, "Cancelled", "renter")).StatusCode);

        var second = await Book(slot.Id, "again@example.com");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task Cannot_book_a_slot_that_has_already_started()
    {
        var slot = await CreateSlot(minutesFromNow: 30);
        _factory.Clock.Advance(TimeSpan.FromMinutes(31));

        var response = await Book(slot.Id, "late@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // --------------------------------------- POST /bookings/{id}/transitions
    [Fact]
    public async Task Cancelling_frees_the_seats_and_is_final()
    {
        var slot = await CreateSlot(capacity: 6);
        var booking = await BookOk(slot.Id, "c@example.com", party: 4);

        var cancel = await Transition(booking.Id, "Cancelled", "owner", "property sold");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Cancelled", (await cancel.Content.ReadFromJsonAsync<BookingResponse>())!.Status);
        Assert.Equal(6, (await GetSlot(slot.Id)).SeatsRemaining);

        var again = await Transition(booking.Id, "Cancelled", "owner");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(6, (await GetSlot(slot.Id)).SeatsRemaining); // seats are not freed twice
    }

    [Fact]
    public async Task Attendance_can_only_be_set_by_the_owner_after_the_slot_starts()
    {
        var slot = await CreateSlot(minutesFromNow: 60);
        var booking = await BookOk(slot.Id, "att@example.com");

        Assert.Equal(HttpStatusCode.Conflict, (await Transition(booking.Id, "Attended", "owner")).StatusCode); // too early

        _factory.Clock.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal(HttpStatusCode.Conflict, (await Transition(booking.Id, "Attended", "renter")).StatusCode); // wrong actor
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(booking.Id, "Cancelled", "renter")).StatusCode); // slot started

        var ok = await Transition(booking.Id, "Attended", "owner");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Attended", (await ok.Content.ReadFromJsonAsync<BookingResponse>())!.Status);

        Assert.Equal(HttpStatusCode.Conflict, (await Transition(booking.Id, "NoShow", "owner")).StatusCode); // final
    }

    [Theory]
    [InlineData("Bogus", "owner")]
    [InlineData("Cancelled", "stranger")]
    [InlineData("", "owner")]
    [InlineData("Cancelled", "")]
    public async Task Transition_with_unknown_values_returns_400(string to, string actor)
    {
        var slot = await CreateSlot();
        var booking = await BookOk(slot.Id, "x@example.com");

        var response = await Transition(booking.Id, to, actor);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transition_to_confirmed_is_rejected_with_409()
    {
        var slot = await CreateSlot();
        var booking = await BookOk(slot.Id, "x@example.com");

        var response = await Transition(booking.Id, "Confirmed", "owner");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Transition_of_an_unknown_booking_returns_404()
    {
        var response = await Transition(Guid.NewGuid(), "Cancelled", "owner");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------ GET /bookings/{id}/history
    [Fact]
    public async Task History_lists_every_state_change_with_actor_reason_and_timestamp()
    {
        var slot = await CreateSlot();
        var booking = await BookOk(slot.Id, "h@example.com");
        await Transition(booking.Id, "Cancelled", "renter", "plans changed");

        var history = await _http.GetFromJsonAsync<List<HistoryResponse>>($"/bookings/{booking.Id}/history");

        Assert.Equal(2, history!.Count);
        Assert.Null(history[0].From);
        Assert.Equal("Confirmed", history[0].To);
        Assert.Equal("Confirmed", history[1].From);
        Assert.Equal("Cancelled", history[1].To);
        Assert.Equal("Renter", history[1].Actor);
        Assert.Equal("plans changed", history[1].Reason);
        Assert.Equal(Now.UtcDateTime, history[1].Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task History_of_an_unknown_booking_returns_404()
    {
        var response = await _http.GetAsync($"/bookings/{Guid.NewGuid()}/history");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------- concurrency
    [Fact]
    public async Task Concurrent_bookings_for_the_last_seats_never_overbook()
    {
        ThreadPool.SetMinThreads(64, 64); // let all requests really run in parallel
        var slot = await CreateSlot(capacity: 6);

        var gate = new TaskCompletionSource();
        var requests = Enumerable.Range(0, 20)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await Book(slot.Id, $"racer{i}@example.com", party: 1);
            }))
            .ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(14, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await GetSlot(slot.Id);
        Assert.Equal(6, after.BookedSeats);
        Assert.Equal(0, after.SeatsRemaining);
    }

    [Fact]
    public async Task Concurrent_cancels_of_one_booking_free_the_seats_only_once()
    {
        ThreadPool.SetMinThreads(64, 64);
        var slot = await CreateSlot(capacity: 6);
        var target = await BookOk(slot.Id, "target@example.com", party: 2);
        await BookOk(slot.Id, "other@example.com", party: 2);

        var gate = new TaskCompletionSource();
        var requests = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await Transition(target.Id, "Cancelled", "renter");
            }))
            .ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(4, (await GetSlot(slot.Id)).SeatsRemaining); // 6 - 2 (other) = 4, not 6
    }
    [Fact]
    public async Task List_bookings_for_a_slot_supports_status_filter()
    {
        var slot = await CreateSlot();
        var a = await BookOk(slot.Id, "a@example.com");
        await BookOk(slot.Id, "b@example.com");
        await Transition(a.Id, "Cancelled", "renter");

        var all = await _http.GetFromJsonAsync<List<BookingResponse>>($"/slots/{slot.Id}/bookings");
        Assert.Equal(2, all!.Count);

        var confirmed = await _http.GetFromJsonAsync<List<BookingResponse>>($"/slots/{slot.Id}/bookings?status=Confirmed");
        Assert.Equal("b@example.com", Assert.Single(confirmed!).RenterEmail);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"/slots/{slot.Id}/bookings?status=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/slots/{Guid.NewGuid()}/bookings")).StatusCode);
    }

    [Fact]
    public async Task List_all_bookings_supports_filters_and_paging()
    {
        var slot = await CreateSlot();
        var email = $"{Guid.NewGuid():N}@example.com"; // unique, so other tests' data doesn't interfere
        var first = await BookOk(slot.Id, email);
        await BookOk(slot.Id, $"{Guid.NewGuid():N}@example.com");
        await Transition(first.Id, "Cancelled", "renter");

        var bySlot = await _http.GetFromJsonAsync<PagedResponse<BookingResponse>>($"/bookings?slotId={slot.Id}");
        Assert.Equal(2, bySlot!.TotalCount);

        var byEmail = await _http.GetFromJsonAsync<PagedResponse<BookingResponse>>(
            $"/bookings?renterEmail={Uri.EscapeDataString(email.ToUpperInvariant())}");
        Assert.Equal(first.Id, Assert.Single(byEmail!.Items).Id);

        var confirmed = await _http.GetFromJsonAsync<PagedResponse<BookingResponse>>(
            $"/bookings?slotId={slot.Id}&status=Confirmed");
        Assert.Equal(1, confirmed!.TotalCount);

        var paged = await _http.GetFromJsonAsync<PagedResponse<BookingResponse>>(
            $"/bookings?slotId={slot.Id}&pageSize=1&page=2");
        Assert.Equal(2, paged!.TotalCount);
        Assert.Single(paged.Items);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/bookings?status=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/bookings?pageSize=101")).StatusCode);
    }
}
