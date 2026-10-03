// Unit test template: use case tests with Moq (Huishoudplanner.Application.Tests).
// Framework: xunit.v3 + AwesomeAssertions + Moq. Naming: MethodName_Scenario_ExpectedResult.
//
// Business rules live in the domain; a use case orchestrates. In a unit test we mock ONLY the
// driven ports (the ForXxx interfaces) and exercise the real use case. Every use case method
// returns a OneOf: assert the success path AND each error variant, that a no-op writes and audits
// nothing, and the audit entry of a real change. Time is a FakeTimeProvider with a fixed instant.
// Illustrative: it need not compile, but the shapes are the rules.

using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OneOf.Types;
using Huishoudplanner.Application.Occurrences;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;

namespace Huishoudplanner.Application.Tests.Occurrences;

public sealed class OccurrenceServiceTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 29, 9, 0, 0, TimeSpan.Zero); // DST change day

    // Driven ports are mocked; the use case under test is the real OccurrenceService.
    private readonly Mock<ForStoringOccurrences> _occurrences = new();
    private readonly Mock<ForRunningTransactions> _transactions = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly OccurrenceService _sut;

    public OccurrenceServiceTemplateTests()
    {
        // The fake transaction runner just runs the work; rollback is proven in the integration tests.
        _transactions
            .Setup(t => t.Run(It.IsAny<Func<CancellationToken, Task<OneOf.OneOf<Success, PortError>>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<OneOf.OneOf<Success, PortError>>> work, CancellationToken ct) => work(ct));

        _sut = new OccurrenceService(_occurrences.Object, _transactions.Object, _time);
    }

    [Fact]
    public async Task Complete_OpenOccurrence_StoresCompletionWithAuditEntry()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var occurrence = TestOccurrence.Open(id: TestIds.Occurrence1, due: new DateOnly(2026, 3, 29));

        _occurrences.Setup(s => s.GetById(occurrence.Id, It.IsAny<CancellationToken>())).ReturnsAsync(occurrence);
        _occurrences
            .Setup(s => s.Replace(It.IsAny<Occurrence>(), It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Success());

        // Act
        var result = await _sut.Complete(TestActors.Planner, occurrence.Id, new CompleteOccurrenceCommand(), ct);

        // Assert: success arm; the write carries the audit entry of the real change.
        result.IsT0.Should().BeTrue();
        _occurrences.Verify(
            s => s.Replace(
                It.Is<Occurrence>(o => o.Status == OccurrenceStatus.Completed),
                It.Is<AuditEntry>(a => a.Actor == TestActors.Planner.ActorId && a.At == Now),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Complete_AlreadyCompleted_ReturnsConflictAndWritesNothing()
    {
        // Arrange: the domain rule (invalid_transition) rejects the change; the store is never asked to write.
        var ct = TestContext.Current.CancellationToken;
        var occurrence = TestOccurrence.Completed(id: TestIds.Occurrence1);
        _occurrences.Setup(s => s.GetById(occurrence.Id, It.IsAny<CancellationToken>())).ReturnsAsync(occurrence);

        // Act
        var result = await _sut.Complete(TestActors.Planner, occurrence.Id, new CompleteOccurrenceCommand(), ct);

        // Assert
        result.IsT2.Should().BeTrue();
        _occurrences.Verify(
            s => s.Replace(It.IsAny<Occurrence>(), It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Complete_UnknownOccurrence_ReturnsNotFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _occurrences
            .Setup(s => s.GetById(TestIds.Occurrence1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotFound("Occurrence", TestIds.Occurrence1.ToString()));

        // Act
        var result = await _sut.Complete(TestActors.Planner, TestIds.Occurrence1, new CompleteOccurrenceCommand(), ct);

        // Assert: NotFound arm.
        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_WhenStoreFails_ReturnsPortErrorWithoutConfigurationValues()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var occurrence = TestOccurrence.Open(id: TestIds.Occurrence1, due: new DateOnly(2026, 3, 29));
        _occurrences.Setup(s => s.GetById(occurrence.Id, It.IsAny<CancellationToken>())).ReturnsAsync(occurrence);
        _occurrences
            .Setup(s => s.Replace(It.IsAny<Occurrence>(), It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PortError("database unavailable"));

        // Act
        var result = await _sut.Complete(TestActors.Planner, occurrence.Id, new CompleteOccurrenceCommand(), ct);

        // Assert: PortError arm; the HTTP adapter maps it to a 503 Problem Details response.
        result.IsT4.Should().BeTrue();
    }
}
