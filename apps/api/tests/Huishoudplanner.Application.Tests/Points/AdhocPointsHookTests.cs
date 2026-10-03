using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>The follow-up the extra, one-off and retract use cases request of the ledger (ADR-0011): reasons <c>recorded</c> and <c>retract</c>.</summary>
public sealed class AdhocPointsHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Day(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static Huishoudplanner.Domain.Identity.Actor As(OccurrenceWorld w) => OccurrenceWorld.Actor(w.P1);

    [Fact]
    public async Task ExtraExecution_recordedAsDoneSyncsWithReasonRecordedAndAPlannedOneDoesNot()
    {
        var w = new OccurrenceWorld();

        var planned = (await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-19")), Ct)).AsT0;
        w.Points.Calls.Should().BeEmpty();
        var done = (await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-16"), Done: true), Ct)).AsT0;

        w.Points.Calls.Should().Equal((done.View.Occurrence.Id, PointsSyncReason.Recorded, w.P1.Id));
        planned.View.Occurrence.Status.Should().Be(OccurrenceStatus.Open);
    }

    [Fact]
    public async Task OneOff_recordedAsDoneSyncsWithReasonRecorded()
    {
        var w = new OccurrenceWorld();

        var done = (await w.Adhoc.CreateOneOffAsync(As(w), new OneOffCommand("Kast opruimen", null, 30, Day("2026-09-16"), Done: true, Points: 12), Ct)).AsT0;
        var planned = (await w.Adhoc.CreateOneOffAsync(As(w), new OneOffCommand("Later", null, 30, Day("2026-09-16")), Ct)).AsT0;

        w.Points.Calls.Should().Equal((done.View.Occurrence.Id, PointsSyncReason.Recorded, w.P1.Id));
        done.View.Occurrence.PointsSnapshot.Should().Be(12);
        planned.View.Occurrence.PointsSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task ReplayOfRecordedWork_syncsAgainSoALostEntryIsRepaired_andCreatesNothingNew()
    {
        var w = new OccurrenceWorld();
        var command = new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-16"), Done: true, RequestId: "points-request-key-0004");
        var first = (await w.Adhoc.CreateExtraAsync(As(w), command, Ct)).AsT0;

        var replay = (await w.Adhoc.CreateExtraAsync(As(w), command, Ct)).AsT0;

        replay.Created.Should().BeFalse();
        w.Points.Calls.Select(c => (c.OccurrenceId, c.Reason)).Should().Equal((first.View.Occurrence.Id, PointsSyncReason.Recorded), (first.View.Occurrence.Id, PointsSyncReason.Recorded));
    }

    [Fact]
    public async Task Retract_syncsWithReasonRetractAfterTheDelete()
    {
        var w = new OccurrenceWorld();
        var done = (await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-16"), Done: true), Ct)).AsT0;

        (await w.Adhoc.RetractAsync(As(w), done.View.Occurrence.Id, Ct)).IsT0.Should().BeTrue();

        w.Points.Calls.Last().Should().Be((done.View.Occurrence.Id, PointsSyncReason.Retract, w.P1.Id));
        w.Occurrences.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Retract_aRefusedRetractNeverSyncs()
    {
        var w = new OccurrenceWorld();
        var planned = (await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-19")), Ct)).AsT0;

        (await w.Adhoc.RetractAsync(As(w), planned.View.Occurrence.Id, Ct)).AsT3.Code.Should().Be("not_retractable");

        w.Points.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ALedgerFailureRollsTheRecordingAndTheRetractBack()
    {
        var w = new OccurrenceWorld();
        w.Points.Failure = new PortError("pointEntries.failed: boom");

        var created = await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-16"), Done: true), Ct);

        created.AsT4.Message.Should().StartWith("pointEntries.failed");
        w.Occurrences.Items.Should().BeEmpty();
        w.Audit.Entries.Should().BeEmpty();

        w.Points.Failure = null;
        var done = (await w.Adhoc.CreateExtraAsync(As(w), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-16"), Done: true), Ct)).AsT0;
        w.Points.Failure = new PortError("pointEntries.failed: boom");
        var auditCount = w.Audit.Entries.Count;

        var retract = await w.Adhoc.RetractAsync(As(w), done.View.Occurrence.Id, Ct);

        retract.AsT5.Message.Should().StartWith("pointEntries.failed");
        w.Occurrences.Items.Should().ContainSingle();
        w.Audit.Entries.Count.Should().Be(auditCount);
    }
}
