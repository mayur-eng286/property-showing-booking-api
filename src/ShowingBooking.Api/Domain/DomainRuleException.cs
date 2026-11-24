namespace ShowingBooking.Api.Domain;

/// <summary>A business rule was violated (maps to HTTP 409).</summary>
public class DomainRuleException(string message) : Exception(message);
