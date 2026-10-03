using System.Net;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Application;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>A PortError message is for logs only: it must reach the log, and never the client.</summary>
public sealed class PortErrorLoggingTests
{
    private sealed class DownPort(string message) : ForCheckingHealth
    {
        public Task<OneOf<Success, PortError>> CheckDatabaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<OneOf<Success, PortError>>(new PortError(message));
    }

    [Fact]
    public async Task HealthService_logs_the_port_error_message_as_a_warning()
    {
        var logs = new LogCollector();
        using var factory = new LoggerFactory([logs]);
        var service = new HealthService(new DownPort("MongoDB is unavailable: TimeoutException"), factory.CreateLogger<HealthService>());

        var report = await service.GetReportAsync(TestContext.Current.CancellationToken);

        report.IsHealthy.Should().BeFalse();
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("MongoDB is unavailable: TimeoutException"));
    }

    [Fact]
    public async Task HealthService_logs_nothing_when_the_database_is_reachable()
    {
        var logs = new LogCollector();
        using var factory = new LoggerFactory([logs]);
        var service = new HealthService(new FakeHealthPort(true), factory.CreateLogger<HealthService>());

        await service.GetReportAsync(TestContext.Current.CancellationToken);

        logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task PortError_becoming_a_500_is_logged_as_an_error_but_not_sent_to_the_client()
    {
        var logs = new LogCollector();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddHttpAdapter();
        await using var app = builder.Build();
        app.UseHttpAdapter();
        app.MapGet("/boom", (ILogger<PortError> logger) => ProblemResults.From(new PortError("cannot reach the store"), logger));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/boom", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("cannot reach");
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Message.Contains("cannot reach the store"));
    }

    [Fact]
    public async Task Health_with_an_unreachable_database_logs_a_warning_without_the_connection_string()
    {
        var logs = new LogCollector();
        using var factory = ApiFactory.ForUnreachableMongo().WithLogProvider(logs);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var warning = logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Category.Contains("HealthService")).Subject;
        warning.Message.Should().NotContain("127.0.0.1").And.NotContain("mongodb://");
    }
}
