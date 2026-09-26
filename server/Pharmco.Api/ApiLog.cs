namespace Pharmco.Api;

/// <summary>
/// Marker type used purely to obtain a typed <see cref="Microsoft.Extensions.Logging.ILogger{T}"/>.
/// The endpoint classes are <c>static</c>, so they cannot be used as the
/// <c>ILogger&lt;T&gt;</c> category argument themselves.
/// </summary>
public sealed class ApiLog { }
