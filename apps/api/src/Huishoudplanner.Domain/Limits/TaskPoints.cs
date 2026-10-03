namespace Huishoudplanner.Domain.Limits;

/// <summary>Points of a task (ADR-0011): the bounds and the default for a duration. Port of <c>points.ts</c>.</summary>
public static class TaskPoints
{
    public const int Min = 0; // MIN_TASK_POINTS

    public const int Max = 1000; // MAX_TASK_POINTS

    /// <summary>
    /// One point per minute, between 1 and <see cref="Max"/> (TS <c>defaultPointsForDuration</c>). The server applies it when
    /// <c>POST /tasks</c> or a one-off task omits <c>points</c>, so the web app never computes it.
    /// </summary>
    public static int DefaultForDuration(int minutes) => Math.Clamp(minutes, 1, Max);
}
