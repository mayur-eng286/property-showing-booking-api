using ShowingBooking.Api.Domain;

namespace ShowingBooking.Tests;

/// <summary>Pure unit tests of the booking state machine: no database, no HTTP, no real clock.</summary>
public class BookingDomainTests
{
    private static readonly DateTime Now = new(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = Now.AddHours(1);

    private static Booking NewBooking()
    {
        var slot = Slot.Create(Guid.NewGuid(), Start, 60, 5, Now);
        return Booking.Create(slot, "renter@example.com", 2, Now);
    }

    [Fact]
    public void New_booking_is_confirmed_and_has_an_initial_history_entry()
    {
        var booking = NewBooking();

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        var entry = Assert.Single(booking.History);
        Assert.Null(entry.FromStatus);
        Assert.Equal(BookingStatus.Confirmed, entry.ToStatus);
    }

    [Fact]
    public void Cannot_book_a_slot_that_has_started()
    {
        var slot = Slot.Create(Guid.NewGuid(), Start, 60, 5, Now);
        Assert.Throws<DomainRuleException>(() => Booking.Create(slot, "a@b.com", 1, Start));
    }

    [Theory]
    [InlineData(Actor.Owner)]
    [InlineData(Actor.Renter)]
    public void Owner_or_renter_can_cancel_before_the_slot_starts(Actor actor)
    {
        var booking = NewBooking();

        booking.Transition(BookingStatus.Cancelled, actor, "changed my mind", Now, Start);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var last = booking.History.Last();
        Assert.Equal(BookingStatus.Confirmed, last.FromStatus);
        Assert.Equal(BookingStatus.Cancelled, last.ToStatus);
        Assert.Equal(actor, last.Actor);
        Assert.Equal("changed my mind", last.Reason);
    }

    [Theory]
    [InlineData(0)]   // exactly at the start time
    [InlineData(30)]  // after the start
    public void Cannot_cancel_once_the_slot_has_started(int minutesAfterStart)
    {
        var booking = NewBooking();
        Assert.Throws<DomainRuleException>(() =>
            booking.Transition(BookingStatus.Cancelled, Actor.Owner, null, Start.AddMinutes(minutesAfterStart), Start));
    }

    [Theory]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.NoShow)]
    public void Owner_can_mark_attended_or_no_show_after_the_slot_starts(BookingStatus target)
    {
        var booking = NewBooking();

        booking.Transition(target, Actor.Owner, null, Start.AddMinutes(5), Start);

        Assert.Equal(target, booking.Status);
    }

    [Theory]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.NoShow)]
    public void Renter_cannot_mark_attendance(BookingStatus target)
    {
        var booking = NewBooking();
        Assert.Throws<DomainRuleException>(() =>
            booking.Transition(target, Actor.Renter, null, Start.AddMinutes(5), Start));
    }

    [Theory]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.NoShow)]
    public void Cannot_mark_attendance_before_the_slot_starts(BookingStatus target)
    {
        var booking = NewBooking();
        Assert.Throws<DomainRuleException>(() =>
            booking.Transition(target, Actor.Owner, null, Now, Start));
    }

    [Fact]
    public void Cannot_transition_back_to_confirmed()
    {
        var booking = NewBooking();
        Assert.Throws<DomainRuleException>(() =>
            booking.Transition(BookingStatus.Confirmed, Actor.Owner, null, Now, Start));
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Attended)]
    [InlineData(BookingStatus.Attended, BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Attended, BookingStatus.NoShow)]
    [InlineData(BookingStatus.NoShow, BookingStatus.Attended)]
    [InlineData(BookingStatus.NoShow, BookingStatus.Confirmed)]
    public void Final_states_cannot_change(BookingStatus firstMove, BookingStatus secondMove)
    {
        var booking = NewBooking();

        // Reach the first (final) state in a legal way.
        if (firstMove == BookingStatus.Cancelled)
            booking.Transition(firstMove, Actor.Owner, null, Now, Start);
        else
            booking.Transition(firstMove, Actor.Owner, null, Start.AddMinutes(5), Start);

        var later = Start.AddMinutes(10);
        Assert.Throws<DomainRuleException>(() =>
            booking.Transition(secondMove, Actor.Owner, null, later, Start));
    }

    [Fact]
    public void Each_transition_bumps_the_version_and_adds_one_history_entry()
    {
        var booking = NewBooking();
        var versionBefore = booking.Version;

        booking.Transition(BookingStatus.Cancelled, Actor.Renter, null, Now, Start);

        Assert.Equal(versionBefore + 1, booking.Version);
        Assert.Equal(2, booking.History.Count);
    }
}
