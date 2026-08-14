namespace TelemetryGuard.Portal.Api;

/// <summary>Outcome of one admin-API call. Error is already human-readable
/// (rendered from the API's problem+json); SessionExpired means the key was
/// rejected (401/403) and the caller must sign the operator out.</summary>
public sealed record AdminApiResult<T>(T? Value, int StatusCode, string? Error, bool SessionExpired)
{
    public bool IsSuccess => Error is null && !SessionExpired;

    public static AdminApiResult<T> Ok(T value) => new(value, 200, null, false);
    public static AdminApiResult<T> Problem(int status, string error) => new(default, status, error, false);
    public static AdminApiResult<T> Rejected() =>
        new(default, 401, "The admin API rejected this API key.", true);
    public static AdminApiResult<T> Unreachable(string detail) =>
        new(default, 0, $"The admin API could not be reached: {detail}", false);
}

/// <summary>Marker for 204/empty-body responses (DELETE).</summary>
public sealed record NoBody;
