namespace Huishoudplanner.Domain.Errors;

/// <summary>The write lost against a concurrent change, or the state the use case read is no longer current.</summary>
public sealed record ConflictError(string Message);
