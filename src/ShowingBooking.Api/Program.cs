using Microsoft.EntityFrameworkCore;
using ShowingBooking.Api;
using ShowingBooking.Api.Application;
using ShowingBooking.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Persistence: SQLite file (see README). Tests override this with a temp file.
var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=showings.db";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// Clock is injected so "before/after the slot starts" rules can be tested with a fake clock.
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

builder.Services.AddScoped<SlotService>();
builder.Services.AddScoped<BookingService>();

// Error handling: one place maps exceptions to 400 / 404 / 409 ProblemDetails responses.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Property Showing Booking API", Version = "v1" }));

var app = builder.Build();

// Simple for a take-home: create the schema on startup (no migrations needed).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.UseExceptionHandler();

// Swagger UI is served at the root so opening http://localhost:5080 gives a browser UI for every endpoint.
app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Property Showing Booking API v1");
    o.RoutePrefix = string.Empty;
});

app.MapShowingApi();

app.Run();

// Needed so the test project can use WebApplicationFactory<Program>.
public partial class Program { }
