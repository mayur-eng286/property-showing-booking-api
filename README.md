# Property Showing Booking API

A small HTTP API where property owners publish showing slots and renters book seats on them.
Built with **C# / .NET 8**, **ASP.NET Core minimal APIs** and **EF Core**.

## Run it

Requirements: .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/ShowingBooking.Api
```

Open **http://localhost:5080** - the Swagger UI is served at the root, so every endpoint can be tried in the browser.
The database file `showings.db` is created automatically in the working directory on first start (delete it to reset).

### Try it in the browser (Swagger UI)
1. `POST /slots` with e.g. `{ "propertyId": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "startsAt": "<a future UTC time, e.g. 2030-01-01T10:00:00Z>", "durationMinutes": 30, "capacity": 6 }` and copy the returned `id`.
2. `POST /slots/{id}/bookings` with `{ "renterEmail": "me@example.com", "partySize": 2 }` and copy the booking `id`.
3. `GET /slots?propertyId=...` - see `seatsRemaining` drop.
4. `POST /bookings/{id}/transitions` with `{ "to": "Cancelled", "reason": "changed plans", "actor": "renter" }`.
5. `GET /bookings/{id}/history` - see every change.

Valid `to` values: `Cancelled`, `Attended`, `NoShow` (`Confirmed` is accepted as input but always rejected with 409). `actor`: `owner` or `renter`.
To test Attended/NoShow by hand you need a slot whose start time is in the future when created, then wait until it starts (the automated tests use a fake clock instead).

## Run the tests

```bash
dotnet test
```

- `BookingDomainTests`: unit tests of the state machine (no database).
- `ApiTests`: end-to-end tests through HTTP, EF Core and a real SQLite file, with a fake clock.
- Two concurrency tests: 20 parallel bookings for 6 seats, and 6 parallel cancels of one booking.

## Persistence
**SQLite** (file `showings.db`) through EF Core. The schema is created with `EnsureCreated()` on startup (no migrations).

## Main design decisions
- **Business rules in the domain object.** `Booking.Transition` holds the entire state machine (states, actor, timing). It takes `now` as a parameter, so it is unit-tested without a clock or database.
- **Injected `TimeProvider`** so "before/after the slot starts" is testable with `FakeTimeProvider`.
- **No overbooking under concurrency.** Seats are reserved with one atomic statement, `UPDATE Slots SET BookedSeats = BookedSeats + n WHERE Id = ? AND BookedSeats + n <= Capacity`, inside the same transaction as the booking insert. 0 rows updated means the slot is full (409). It is the first statement of the transaction so SQLite takes its write lock immediately and waiting requests queue instead of failing.
- **Database as a safety net.** A `CHECK (BookedSeats BETWEEN 0 AND Capacity)` constraint and a partial unique index `(SlotId, RenterEmail) WHERE Status='Confirmed'` enforce the two invariants even if application code had a bug.
- **Optimistic concurrency on bookings** (`Version` token): two simultaneous cancels cannot both succeed, so seats are never freed twice.
- **History is written in the same transaction** as each state change, so it can never disagree with the booking.
- **Errors** are RFC 7807 ProblemDetails, produced in one place (`ApiExceptionHandler`): 400 bad input, 404 missing, 409 rule violations.
- **Folders as layers** (Domain / Application / Infrastructure) in one project, to keep it small. No MediatR: optional per the brief and unnecessary at this size.

### Assumptions
- No authentication (the brief passes `actor` in the body), so the API cannot check that a "renter" owns the booking they cancel.
- Booking a slot that already started is rejected (409).
- "Before the slot starts" means `now < startsAt`.
- Only Confirmed bookings are active and hold seats; cancelling frees them.
- Emails are trimmed and lower-cased before comparison.
- `pageSize` above 100 is rejected with 400 (not clamped). All times are UTC.
- Extra endpoints beyond the brief: `GET /slots/{id}` and `GET /bookings/{id}`.


## AI use

**What I used AI (Claude) for**
- Breaking the brief into requirements, edge cases and assumptions.
- Proposing the architecture, data model and concurrency approach, and a test plan.
- Generating the first version of the code (domain, services, endpoints, EF mapping) and the test suite.
- Drafting this README and `docs/PLAN.md`.
