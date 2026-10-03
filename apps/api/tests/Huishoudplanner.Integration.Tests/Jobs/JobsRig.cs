using Huishoudplanner.Adapters.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>A job that records its runs and can be held, for the scheduler and runner tests.</summary>
internal sealed class RecordingJob(string name, string schedule, Func<CancellationToken, Task<JobOutcome>>? body = null) : IJob
{
    public string Name => name;

    public string Schedule => schedule;

    public int Runs { get; private set; }

    public IServiceProvider? LastServices { get; private set; }

    public Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        Runs++;
        LastServices = services;
        return body is null ? Task.FromResult(JobOutcome.Succeeded) : body(cancellationToken);
    }
}

/// <summary>A runner, a scheduler and a fake clock without a host: the time of every test is moved by hand.</summary>
internal sealed class JobsRig : IDisposable
{
    public const string Amsterdam = "Europe/Amsterdam";

    private readonly ServiceProvider provider;

    public JobsRig(DateTimeOffset now, ILoggerProvider? logs = null)
    {
        Clock = new FakeTimeProvider(now);
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        provider = services.BuildServiceProvider();
        Runner = new JobRunner(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<JobRunner>>());
    }

    public FakeTimeProvider Clock { get; }

    public JobRunner Runner { get; }

    public JobScheduler Scheduler(bool enabled, params IJob[] jobs) =>
        new(jobs, Runner, Clock, new JobsOptions(enabled, TimeZoneInfo.FindSystemTimeZoneById(Amsterdam)));

    public void Dispose() => provider.Dispose();
}
