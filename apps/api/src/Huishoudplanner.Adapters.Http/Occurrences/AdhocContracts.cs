using System.ComponentModel;

namespace Huishoudplanner.Adapters.Http.Occurrences;

// The request records below only document the bodies in OpenAPI; the bodies are read by OccurrenceRequestParser (Zod-style
// validation_error for a wrong type, null, malformed JSON or a bad day), never bound to these types.

/// <summary>An extra execution of an existing task: planned on a generated day, or recorded as already done today.</summary>
public sealed record CreateExtraExecutionRequest(
    [property: Description("The task, which must exist and be active.")] string? TaskId,
    [property: Description("The day, YYYY-MM-DD, within a generated cycle. With done: today.")] string? Date,
    [property: Description("An active person, or null for anyone. Left out: the task's default assignee, the actor when done. Done needs a person.")] string? AssigneeId,
    [property: Description("Record the work as already done; only allowed for today.")] bool? Done,
    [property: Description("Idempotency key, 16 to 64 characters of letters, digits, underscore and hyphen.")] string? RequestId);

/// <summary>A one-off task: work done once, with no task record. Name, duration and room live on the occurrence only.</summary>
public sealed record CreateOneOffRequest(
    [property: Description("Trimmed, 1 to 120 characters.")] string? Name,
    [property: Description("An active room, or null or left out for none.")] string? RoomId,
    [property: Description("Whole minutes, at least 1.")] int? DurationMinutes,
    [property: Description("The day, YYYY-MM-DD, within a generated cycle. With done: today.")] string? Date,
    [property: Description("An active person, or null for anyone. Left out: nobody, the actor when done. Done needs a person.")] string? AssigneeId,
    [property: Description("Record the work as already done; only allowed for today.")] bool? Done,
    [property: Description("Points, a whole number from 0 to 1000; left out: one per minute of the duration.")] int? Points,
    [property: Description("Idempotency key, 16 to 64 characters of letters, digits, underscore and hyphen.")] string? RequestId);

/// <summary>The answer of a retract.</summary>
public sealed record RetractOccurrenceResponse(bool Retracted, string Id);
