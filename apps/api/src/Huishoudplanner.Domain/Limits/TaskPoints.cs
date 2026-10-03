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

    /// <summary>
    /// The same for a duration that is not a whole number of minutes, with the rounding of JavaScript's <c>Math.round</c>: halves
    /// round up (2.5 gives 3, not the banker's 2 of <see cref="Math.Round(double)"/>). NaN and infinities give 1, like
    /// <c>Number.isFinite</c> in TypeScript.
    /// </summary>
    public static int DefaultForDuration(double minutes)
    {
        if (!double.IsFinite(minutes))
        {
            return 1;
        }

        var floor = Math.Floor(minutes);
        var rounded = minutes - floor >= 0.5 ? floor + 1 : floor;
        return (int)Math.Min(Max, Math.Max(1, rounded));
    }
}
