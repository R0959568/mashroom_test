namespace CitibikeIngest.Api.Models;

// Managers throw these; ExceptionMiddleware maps them to 404 / 409 / 400 / 502 / 503 with the message as text body.

public sealed class NotFoundException(string message) : Exception(message);

public sealed class ConflictException(string message) : Exception(message);

public sealed class ValidationException(string message) : Exception(message);

/// <summary>An external source (such as NOAA or the forecast API) failed or answered with something unusable.</summary>
public sealed class UpstreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A feature whose settings are missing in this deployment (the forecast API without a URL and a key).</summary>
public sealed class NotConfiguredException(string message) : Exception(message);
