namespace ShowingBooking.Api.Application;

/// <summary>Bad input (HTTP 400).</summary>
public class RequestValidationException(IDictionary<string, string[]> errors) : Exception("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Resource does not exist (HTTP 404).</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>Request is valid but not allowed in the current state (HTTP 409).</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>Collects field errors, then throws one RequestValidationException.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = new List<string>();
        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
            throw new RequestValidationException(_errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }
}
