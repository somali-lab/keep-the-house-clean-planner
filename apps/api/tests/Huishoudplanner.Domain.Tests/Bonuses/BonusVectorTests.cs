using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tests.Support;
using static Huishoudplanner.Domain.Tests.Support.PointsVectorArguments;

namespace Huishoudplanner.Domain.Tests.Bonuses;

/// <summary>Runs every case of Vectors/bonuses.json against <see cref="BonusSchedule"/>, <see cref="Period"/>, <see cref="BonusKinds"/> and <see cref="BonusCalculator"/>.</summary>
public class BonusVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("bonuses");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_151_cases() => Cases.Count.Should().Be(151);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "bonusAmountsOn" => BonusSchedule.AmountsOn(ReadSchedule(c.Input.GetProperty("schedule")), c.Day("day")),
        "sameBonusAmounts" => BonusSchedule.SameAmounts(ReadAmounts(c.Input.GetProperty("a")), ReadAmounts(c.Input.GetProperty("b"))),
        "scheduleWithAmounts" => BonusSchedule
            .WithAmounts(ReadSchedule(c.Input.GetProperty("schedule")), ReadAmounts(c.Input.GetProperty("amounts")), c.Day("today"))
            .Select(r => new { From = Day(r.From), r.Amounts.WeekDone, r.Amounts.WeekOnTime, r.Amounts.CycleDone, r.Amounts.CycleOnTime })
            .ToList(),
        "isBonusKind" => BonusKinds.IsBonusKind(c.Text("kind")),
        "weekOf" => Shape(Period.WeekOf(c.Day("day"))),
        "cycleOf" => Shape(Period.CycleOf(c.Day("day"), c.Day("anchor"))),
        "periodEnded" => PeriodEnding(c).HasEnded(c.Day("today")),
        "onTimeCutoff" => PeriodEnding(c).OnTimeCutoff(c.Zone("timezone")),
        "periodDayOf" => ReadOccurrence(c.Input.GetProperty("occurrence")).PeriodDay,
        "periodOwnerOf" => ReadOccurrence(c.Input.GetProperty("occurrence")).PeriodOwnerId,
        "creditedOf" => ReadOccurrence(c.Input.GetProperty("occurrence")).CreditedId,
        "placementsOf" => BonusCalculator.PlacementsOf(ReadOccurrence(c.Input.GetProperty("occurrence")))
            .Select(p => new
            {
                p.Person,
                Status = StatusName(p.Item.Status),
                Date = Day(p.Item.Date),
                p.Item.RecordedDone,
                p.Item.CompletedBy,
                CompletedAt = Iso(p.Item.CompletedAt),
            })
            .ToList(),
        "evaluateSet" => BonusCalculator.EvaluateSet(ReadOccurrences(c.Input.GetProperty("set")), c.Instant("cutoff")),
        "bonusKey" => BonusCalculator.BonusKey(Kind(c.Text("kind")), c.Text("personId"), c.Day("periodStart")),
        "expectedBonusEntries" => BonusCalculator
            .ExpectedEntries(ReadOccurrences(c.Input.GetProperty("items")), ReadContext(c.Input.GetProperty("context")))
            .Select(e => new
            {
                e.Key,
                Kind = e.Kind.ToLedgerKind(),
                e.PersonId,
                e.Amount,
                PeriodStart = Day(e.PeriodStart),
                PeriodEnd = Day(e.PeriodEnd),
            })
            .ToList(),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    /// <summary>The vectors pass <c>{ end }</c> only (a TypeScript <c>Pick</c>); the other fields do not matter for these rules.</summary>
    private static Period PeriodEnding(VectorCase c)
    {
        var end = DayKeys.Parse(c.Input.GetProperty("period").GetProperty("end").GetString()!);
        return new Period(PeriodUnit.Week, end, end);
    }

    private static object Shape(Period p) => new { Unit = p.Unit == PeriodUnit.Week ? "week" : "cycle", Start = Day(p.Start), End = Day(p.End) };

    private static string StatusName(OccurrenceStatus status) => status switch
    {
        OccurrenceStatus.Open => "open",
        OccurrenceStatus.Done => "done",
        _ => "skipped",
    };

    private static BonusKind Kind(string ledgerKind) =>
        BonusKinds.TryParse(ledgerKind, out var kind) ? kind : throw new ArgumentOutOfRangeException(nameof(ledgerKind), ledgerKind, "Unknown bonus kind.");
}
