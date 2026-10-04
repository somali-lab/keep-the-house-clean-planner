using System.Text;
using Huishoudplanner.Application.Statistics;
using Huishoudplanner.Application.Tests.Statistics;
using Huishoudplanner.Application.Transfer;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Transfer;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OneOf;

namespace Huishoudplanner.Application.Tests.Transfer;

/// <summary>
/// The export and import use cases with in-memory ports (<c>routes/transfer.ts</c> and <c>domain/transfer.ts</c> of the Node server): the order of the
/// refusals, the acknowledgements of a file that would remove redemptions or badges, one transaction for the replacement and its audit entry, and the
/// rebuild of the ledger after the commit.
/// </summary>
public sealed class TransferServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 22, 30, 0, TimeSpan.Zero);

    private static readonly Actor Admin = new("0123456789abcdef01234567", Role.Admin, ActorSource.Ui);

    private static readonly ImportOptions Confirmed = new("replace", "true", null, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeParsed(int version) : ParsedImport(version, "2026-09-16T08:00:00.000Z");

    private sealed class World
    {
        public World(int version = 6, ExistingCounts? existing = null)
        {
            Audit = new FakeAudit();
            Transactions = new FakeTransactions(Audit);
            Parsed = new FakeParsed(version);
            Data.Setup(d => d.ParseAsync(It.IsAny<Stream>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ParseFailure is { } failure ? failure : OneOf<ParsedImport, ValidationErrors, PortError>.FromT0(Parsed));
            Data.Setup(d => d.CountExistingAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(OneOf<ExistingCounts, PortError>.FromT0(existing ?? new ExistingCounts(0, 0)));
            Data.Setup(d => d.ReplaceAsync(It.IsAny<ParsedImport>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    AuditEntriesWhenReplaced = Audit.Entries.Count;
                    return Task.FromResult(ReplaceFailure is { } failure ? failure : OneOf<ImportResult, PortError>.FromT0(Replaced));
                });
            Settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SettingsTimezone is { } tz
                    ? OneOf<HouseholdSettings, SettingsMissing, PortError>.FromT0(SettingsDefaults.ForNewInstallation(tz, new DateOnly(2026, 9, 14), Now))
                    : OneOf<HouseholdSettings, SettingsMissing, PortError>.FromT1(new SettingsMissing()));
            Points.Setup(p => p.RecomputeAsync(It.IsAny<AuditActor>(), It.IsAny<PointsRecomputeTrigger>(), It.IsAny<CancellationToken>()))
                .Returns(() => RecomputeThrows
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(OneOf<PointsRecomputeResult, ConflictError, PortError>.FromT0(PointsRecomputeResult.Empty(PointsRecomputeTrigger.Import))));
            Service = new TransferService(Data.Object, Settings.Object, new HouseholdOptions("Europe/Amsterdam"), Transactions, Audit, Points.Object, new FixedClock(Now), NullLogger<TransferService>.Instance);
        }

        public static ImportResult Counts { get; } = new(new ReplacedCounts(1, 3, 2, 4, 1, 2, 9, 1, 2), 7, 5, 1, 0, 0);

        public ImportResult Replaced { get; set; } = Counts;

        public string? SettingsTimezone { get; set; } = "Europe/Amsterdam";

        public bool RecomputeThrows { get; set; }

        public OneOf<ParsedImport, ValidationErrors, PortError>? ParseFailure { get; set; }

        public PortError? ReplaceFailure { get; set; }

        public int AuditEntriesWhenReplaced { get; private set; } = -1;

        public FakeParsed Parsed { get; }

        public Mock<ForTransferringData> Data { get; } = new();

        public Mock<ForStoringSettings> Settings { get; } = new();

        public Mock<IPointsService> Points { get; } = new();

        public FakeAudit Audit { get; }

        public FakeTransactions Transactions { get; }

        public TransferService Service { get; }

        public Task<OneOf<ImportResult, ValidationErrors, ConfirmationRequired, ConflictError, PortError>> ImportAsync(ImportOptions options) =>
            Service.ImportAsync(Admin, options, new MemoryStream(Encoding.UTF8.GetBytes("{}")), Ct);

        public void NothingWritten()
        {
            Data.Verify(d => d.ReplaceAsync(It.IsAny<ParsedImport>(), It.IsAny<CancellationToken>()), Times.Never);
            Audit.Entries.Should().BeEmpty();
            Points.Verify(p => p.RecomputeAsync(It.IsAny<AuditActor>(), It.IsAny<PointsRecomputeTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // ---- export

    [Fact]
    public async Task Export_answersTheFileNamedAfterTodayInTheHouseholdTimezone()
    {
        var world = new World();
        world.Data.Setup(d => d.ExportAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync(OneOf<byte[], PortError>.FromT0([1, 2, 3]));
        world.SettingsTimezone = "Europe/Amsterdam"; // 22:30 UTC is already the next day there

        var result = await world.Service.ExportAsync(Ct);

        var file = result.AsT0;
        file.FileName.Should().Be("huishoudplanner-20260917.json");
        file.ContentType.Should().Be("application/json");
        file.Content.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Export_withoutSettings_fallsBackToTheConfiguredTimezone_andFailsOnAPortError()
    {
        var world = new World { SettingsTimezone = null };
        world.Data.Setup(d => d.ExportAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync(OneOf<byte[], PortError>.FromT0([1]));

        (await world.Service.ExportAsync(Ct)).AsT0.FileName.Should().Be("huishoudplanner-20260917.json");

        world.Data.Setup(d => d.ExportAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync(new PortError("mongo down"));
        (await world.Service.ExportAsync(Ct)).AsT1.Message.Should().Be("mongo down");
    }

    // ---- refusals, in the order of the Node route

    [Theory]
    [InlineData(null, "required")]
    [InlineData("merge", "invalid_enum")]
    [InlineData("REPLACE", "invalid_enum")]
    public async Task Import_needsTheReplaceMode_beforeAnythingElse(string? mode, string code)
    {
        var world = new World();

        var result = await world.ImportAsync(new ImportOptions(mode, null, null, null));

        result.AsT1.Errors.Should().ContainKey("mode").WhoseValue.Should().Equal(code);
        world.Data.Verify(d => d.ParseAsync(It.IsAny<Stream>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        world.NothingWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public async Task Import_withoutTheConfirmation_isRefusedBeforeTheFileIsRead(string? confirm)
    {
        var world = new World();

        var result = await world.ImportAsync(new ImportOptions("replace", confirm, "true", "true"));

        result.IsT2.Should().BeTrue();
        world.Data.Verify(d => d.ParseAsync(It.IsAny<Stream>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        world.NothingWritten();
    }

    [Fact]
    public async Task Import_ofAFileThatIsNotValid_answersTheFieldErrors_andWritesNothing()
    {
        var errors = ValidationErrors.For("collections.users.0.color", "invalid_color");
        var world = new World { ParseFailure = errors };

        var result = await world.ImportAsync(Confirmed);

        result.AsT1.Should().Be(errors);
        world.Data.Verify(d => d.CountExistingAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.NothingWritten();
    }

    [Fact]
    public async Task Import_judgesTheBonusScheduleAgainstTheClock()
    {
        var world = new World();

        await world.ImportAsync(Confirmed);

        world.Data.Verify(d => d.ParseAsync(It.IsAny<Stream>(), Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Import_ofAFileOlderThanVersion5_whileRedemptionsExist_needsTheAcknowledgement(int version)
    {
        var world = new World(version, new ExistingCounts(3, 0));

        var result = await world.ImportAsync(Confirmed);

        var conflict = result.AsT3;
        conflict.Code.Should().Be("redemptions_would_be_removed");
        conflict.Extensions.Should().ContainKey("count").WhoseValue.Should().Be(3);
        world.NothingWritten();
    }

    [Fact]
    public async Task Import_ofAFileOlderThanVersion6_whileBadgesExist_needsItsOwnAcknowledgement()
    {
        var world = new World(5, new ExistingCounts(0, 2));

        var result = await world.ImportAsync(Confirmed);

        var conflict = result.AsT3;
        conflict.Code.Should().Be("badges_would_be_removed");
        conflict.Extensions.Should().ContainKey("count").WhoseValue.Should().Be(2);
        world.NothingWritten();
    }

    [Fact]
    public async Task Import_asksForTheRedemptionsFirst_andForTheBadgesOnlyWhenThatOneIsAcknowledged()
    {
        var world = new World(4, new ExistingCounts(3, 2));

        (await world.ImportAsync(Confirmed)).AsT3.Code.Should().Be("redemptions_would_be_removed");
        (await world.ImportAsync(Confirmed with { AcknowledgeRedemptions = "true" })).AsT3.Code.Should().Be("badges_would_be_removed");
        (await world.ImportAsync(Confirmed with { AcknowledgeRedemptions = "true", AcknowledgeBadges = "true" })).IsT0.Should().BeTrue();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public async Task Import_takesOnlyTheExactTextTrueAsAnAcknowledgement(string value)
    {
        var world = new World(4, new ExistingCounts(3, 0));

        (await world.ImportAsync(Confirmed with { AcknowledgeRedemptions = value })).IsT3.Should().BeTrue();
    }

    [Fact]
    public async Task Import_ofAVersion6File_orWithNothingToLose_needsNoAcknowledgement()
    {
        (await new World(6, new ExistingCounts(3, 2)).ImportAsync(Confirmed)).IsT0.Should().BeTrue();
        (await new World(1, new ExistingCounts(0, 0)).ImportAsync(Confirmed)).IsT0.Should().BeTrue();
        (await new World(5, new ExistingCounts(3, 0)).ImportAsync(Confirmed)).IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Import_whenTheCountsCannotBeRead_failsBeforeWriting()
    {
        var world = new World(4);
        world.Data.Setup(d => d.CountExistingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PortError("count failed"));

        var result = await world.ImportAsync(Confirmed);

        result.AsT4.Message.Should().Be("count failed");
        world.NothingWritten();
    }

    // ---- the replacement

    [Fact]
    public async Task Import_replacesTheData_andRecordsOneAuditEntryInTheSameTransaction()
    {
        var world = new World();

        var result = await world.ImportAsync(Confirmed);

        result.AsT0.Should().Be(World.Counts);
        world.Transactions.Runs.Should().Be(1);
        world.AuditEntriesWhenReplaced.Should().Be(0, "the entry is written after the replacement, inside the same transaction");
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Entity.Should().Be(AuditEntity.Import);
        entry.Action.Should().Be(AuditAction.Create);
        entry.Actor.Should().Be(AuditActor.From(Admin));
        entry.After["removedRedemptions"].Should().Be((AuditValue)1);
        entry.After["occurrences"].Should().Be((AuditValue)9);
        entry.Meta!["schemaVersion"].Should().Be((AuditValue)6);
        entry.Meta["exportedAt"].Should().Be((AuditValue)"2026-09-16T08:00:00.000Z");
        entry.Meta["mode"].Should().Be((AuditValue)"replace");
    }

    [Fact]
    public async Task Import_rebuildsTheLedgerAfterTheCommit_asTheImportTrigger()
    {
        var world = new World();

        await world.ImportAsync(Confirmed);

        world.Points.Verify(p => p.RecomputeAsync(AuditActor.From(Admin), PointsRecomputeTrigger.Import, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_stillSucceeds_whenTheRebuildFails()
    {
        var world = new World { RecomputeThrows = true };

        var result = await world.ImportAsync(Confirmed);

        result.IsT0.Should().BeTrue();
        world.Audit.Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_whenTheReplacementFails_rollsBack_andRebuildsNothing()
    {
        var world = new World { ReplaceFailure = new PortError("mongo.failed") };

        var result = await world.ImportAsync(Confirmed);

        result.AsT4.Message.Should().Be("mongo.failed");
        world.Transactions.Aborts.Should().Be(1);
        world.Audit.Entries.Should().BeEmpty();
        world.Points.Verify(p => p.RecomputeAsync(It.IsAny<AuditActor>(), It.IsAny<PointsRecomputeTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_whenTheAuditEntryCannotBeWritten_rollsBackTheReplacement()
    {
        var world = new World();
        world.Audit.Failure = new PortError("audit down");

        var result = await world.ImportAsync(Confirmed);

        result.AsT4.Message.Should().Be("audit down");
        world.Transactions.Aborts.Should().Be(1);
        world.Points.Verify(p => p.RecomputeAsync(It.IsAny<AuditActor>(), It.IsAny<PointsRecomputeTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_whenWritersKeepConflicting_answersTheConflict()
    {
        var world = new World();
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        var result = await world.ImportAsync(Confirmed);

        result.AsT3.Code.Should().Be("write_conflict");
        world.NothingWritten();
    }
}
