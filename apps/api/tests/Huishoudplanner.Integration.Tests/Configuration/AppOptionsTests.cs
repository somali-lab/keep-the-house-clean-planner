using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Integration.Tests.Configuration;

// Ports apps/server/test/config.test.ts. No Docker and no WebApplicationFactory: plain
// ConfigurationBuilder and ServiceCollection, so these run anywhere (0.4b owns the HTTP fixture).
public class AppOptionsTests
{
    private const string Now = "2026-09-16T08:00:00Z";

    private static readonly Dictionary<string, string?> Base = new() { ["MONGO_URL"] = "mongodb://localhost:27017/x" };

    private static ServiceProvider Provider(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).AddLegacyEnvironmentAliases().Build();
        return new ServiceCollection().AddAppOptions(configuration).BuildServiceProvider();
    }

    private static AppOptions Load(params (string Key, string? Value)[] extra)
    {
        using var provider = Provider(With(extra));
        return provider.GetRequiredService<IOptions<AppOptions>>().Value;
    }

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>(Base);
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return values;
    }

    private static string FailureMessage(IDictionary<string, string?> values)
    {
        using var provider = Provider(values);
        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AppOptions>>().Value);
        return thrown.Message;
    }

    private static string FailureMessage(params (string Key, string? Value)[] extra) => FailureMessage(With(extra));

    [Fact]
    public void Load_onlyMongoUrl_appliesDefaults()
    {
        var options = Load();

        options.Port.Should().Be(3000);
        options.Timezone.Should().Be("Europe/Amsterdam");
        options.SeedUsers.Should().BeEquivalentTo(AppOptions.DefaultSeedUsers);
        options.AuditRetentionDays.Should().BeNull();
        options.DisableScheduler.Should().BeFalse();
        options.NotifyType.Should().Be(NotifyType.None);
        options.Environment.Should().Be(AppEnvironment.Production);
        options.LogLevel.Should().Be(LogLevel.Information);
        options.MongoUrl.Should().Be("mongodb://localhost:27017/x");
        options.AiApiKey.Should().BeNull();
        options.FakeNow.Should().BeNull();
        options.WebDistDir.Should().BeNull();
    }

    [Fact]
    public void Load_emptyStrings_areTreatedAsUnset()
    {
        var options = Load(("SEED_USERS", ""), ("AUDIT_RETENTION_DAYS", ""), ("PORT", ""), ("NOTIFY_TYPE", ""), ("APP_FAKE_NOW", ""));

        options.SeedUsers.Should().BeEquivalentTo(AppOptions.DefaultSeedUsers);
        options.AuditRetentionDays.Should().BeNull();
        options.Port.Should().Be(3000);
        options.NotifyType.Should().Be(NotifyType.None);
        options.FakeNow.Should().BeNull();
    }

    [Fact]
    public void Load_seedUsersJson_isParsed()
    {
        var options = Load(("SEED_USERS", """[{"name":"A","color":"#000000"},{"name":" B ","color":"#FFFFFF"},{"name":"C","color":"#123456"}]"""));

        options.SeedUsers.Select(u => u.Name).Should().Equal("A", "B", "C");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""[{"name":"","color":"#000000"}]""")]
    [InlineData("""[{"name":"A","color":"red"}]""")]
    [InlineData("""[{"name":"A"}]""")]
    [InlineData("""[{"name":1,"color":"#000000"}]""")]
    public void Load_invalidSeedUsers_failsNamingTheVariable(string seedUsers)
    {
        FailureMessage(("SEED_USERS", seedUsers)).Should().Contain("SEED_USERS");
    }

    [Fact]
    public void Load_missingOrEmptyMongoUrl_fails()
    {
        FailureMessage(new Dictionary<string, string?>()).Should().Contain("MONGO_URL");
        FailureMessage(("MONGO_URL", "")).Should().Contain("MONGO_URL");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("1.5")]
    public void Load_invalidPort_fails(string port)
    {
        FailureMessage(("PORT", port)).Should().Contain("PORT");
    }

    [Fact]
    public void Load_validPort_isUsed()
    {
        Load(("PORT", "8080")).Port.Should().Be(8080);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void Load_invalidAuditRetention_fails(string days)
    {
        FailureMessage(("AUDIT_RETENTION_DAYS", days)).Should().Contain("AUDIT_RETENTION_DAYS");
    }

    [Fact]
    public void Load_auditRetention_isParsed()
    {
        Load(("AUDIT_RETENTION_DAYS", "30")).AuditRetentionDays.Should().Be(30);
    }

    [Fact]
    public void Load_notifyTypeWithoutUrl_failsNamingNotifyUrl()
    {
        FailureMessage(("NOTIFY_TYPE", "ntfy")).Should().Contain("NOTIFY_URL");
        FailureMessage(("NOTIFY_TYPE", "homeassistant")).Should().Contain("NOTIFY_URL");
    }

    [Fact]
    public void Load_notifyTypeWithUrl_isAccepted()
    {
        var options = Load(("NOTIFY_TYPE", "homeassistant"), ("NOTIFY_URL", "https://ha.local/api"), ("NOTIFY_TOKEN", "t"));

        options.NotifyType.Should().Be(NotifyType.HomeAssistant);
        options.NotifyUrl.Should().Be("https://ha.local/api");
        options.NotifyToken.Should().Be("t");
    }

    [Theory]
    [InlineData("/foo")]
    [InlineData("ftp://host/x")]
    [InlineData("mailto:a@b.c")]
    [InlineData("host:8080")]
    public void Load_notifyUrlWithoutHttpScheme_fails(string url)
    {
        FailureMessage(("NOTIFY_URL", url)).Should().Contain("NOTIFY_URL");
    }

    [Fact]
    public void Load_dotNetEnvironmentVariable_isFallbackBeforeNodeEnv()
    {
        Load(("DOTNET_ENVIRONMENT", "Development"), ("NODE_ENV", "production")).Environment
            .Should().Be(AppEnvironment.Development);
        Load(("ASPNETCORE_ENVIRONMENT", "Test"), ("DOTNET_ENVIRONMENT", "Development")).Environment
            .Should().Be(AppEnvironment.Test);
    }

    [Fact]
    public void Load_unknownNotifyType_fails()
    {
        FailureMessage(("NOTIFY_TYPE", "sms")).Should().Contain("NOTIFY_TYPE");
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Load_invalidSchedulerFlag_fails(string flag)
    {
        FailureMessage(("DISABLE_SCHEDULER", flag)).Should().Contain("DISABLE_SCHEDULER");
    }

    [Fact]
    public void Load_schedulerFlagTrue_disablesScheduler()
    {
        Load(("DISABLE_SCHEDULER", "true")).DisableScheduler.Should().BeTrue();
    }

    [Fact]
    public void Load_aiAndWebSettings_arePassedThrough()
    {
        var options = Load(("AI_API_KEY", "k"), ("WEB_DIST_DIR", "/srv/web"), ("TZ_APP", "UTC"));

        options.AiApiKey.Should().Be("k");
        options.WebDistDir.Should().Be("/srv/web");
        options.Timezone.Should().Be("UTC");
    }

    [Fact]
    public void Load_fakeNowOutsideTestEnvironment_failsNamingTheVariable()
    {
        FailureMessage(("APP_FAKE_NOW", Now)).Should().Contain("APP_FAKE_NOW");
        FailureMessage(("APP_FAKE_NOW", Now), ("ASPNETCORE_ENVIRONMENT", "Development")).Should().Contain("APP_FAKE_NOW");
    }

    [Fact]
    public void Load_fakeNowInTestEnvironment_isParsed()
    {
        var options = Load(("ASPNETCORE_ENVIRONMENT", "Test"), ("APP_FAKE_NOW", Now));

        options.FakeNow.Should().Be(DateTimeOffset.Parse(Now, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2026-09-16T08:00:00")]
    [InlineData("2026-09-16")]
    [InlineData("yesterday")]
    public void Load_fakeNowWithoutOffsetOrNotAnInstant_fails(string value)
    {
        FailureMessage(("ASPNETCORE_ENVIRONMENT", "Test"), ("APP_FAKE_NOW", value)).Should().Contain("APP_FAKE_NOW");
    }

    [Fact]
    public void Load_fakeNowWithNumericOffset_isAccepted()
    {
        Load(("ASPNETCORE_ENVIRONMENT", "test"), ("APP_FAKE_NOW", "2026-09-16T10:00:00+02:00")).FakeNow
            .Should().Be(DateTimeOffset.Parse(Now, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Load_nodeEnvAlias_isReadWhenAspNetCoreEnvironmentIsUnset()
    {
        Load(("NODE_ENV", "development")).Environment.Should().Be(AppEnvironment.Development);
        Load(("NODE_ENV", "test"), ("APP_FAKE_NOW", Now)).FakeNow.Should().NotBeNull();
    }

    [Fact]
    public void Load_aspNetCoreEnvironment_winsOverNodeEnv()
    {
        Load(("ASPNETCORE_ENVIRONMENT", "Development"), ("NODE_ENV", "production")).Environment
            .Should().Be(AppEnvironment.Development);
    }

    [Fact]
    public void Load_unknownEnvironment_fails()
    {
        FailureMessage(("ASPNETCORE_ENVIRONMENT", "Staging")).Should().Contain("ASPNETCORE_ENVIRONMENT");
        FailureMessage(("NODE_ENV", "staging")).Should().Contain("NODE_ENV");
    }

    [Theory]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("silent", LogLevel.None)]
    public void Load_legacyLogLevelAlias_isMapped(string value, LogLevel expected)
    {
        Load(("LOG_LEVEL", value)).LogLevel.Should().Be(expected);
    }

    [Fact]
    public void Load_dotNetLogLevel_winsOverLegacyAlias()
    {
        Load(("Logging:LogLevel:Default", "Warning"), ("LOG_LEVEL", "debug")).LogLevel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Load_unknownLogLevel_fails()
    {
        FailureMessage(("LOG_LEVEL", "loud")).Should().Contain("LOG_LEVEL");
    }

    [Fact]
    public void AddLegacyEnvironmentAliases_legacyLogLevel_feedsTheLoggingConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_LEVEL"] = "warn" })
            .AddLegacyEnvironmentAliases()
            .Build();

        configuration["Logging:LogLevel:Default"].Should().Be("Warning");
    }

    [Fact]
    public void ResolveLegacyHostEnvironment_nodeEnvOnly_mapsToHostEnvironmentName()
    {
        AppOptionsBinder.ResolveLegacyHostEnvironment(k => k == "NODE_ENV" ? "development" : null).Should().Be("Development");
        AppOptionsBinder.ResolveLegacyHostEnvironment(k => k == "NODE_ENV" ? "test" : null).Should().Be("Test");
        AppOptionsBinder.ResolveLegacyHostEnvironment(k => k == "NODE_ENV" ? "production" : null).Should().Be("Production");
    }

    [Fact]
    public void ResolveLegacyHostEnvironment_aspNetCoreEnvironmentSet_leavesTheHostAlone()
    {
        AppOptionsBinder.ResolveLegacyHostEnvironment(k => k is "NODE_ENV" or "ASPNETCORE_ENVIRONMENT" ? "development" : null)
            .Should().BeNull();
    }

    [Fact]
    public void Validation_invalidConfiguration_doesNotEchoValues()
    {
        const string secret = "secret-token-value";
        var values = With(
            ("NOTIFY_URL", secret),
            ("PORT", secret),
            ("SEED_USERS", secret),
            ("DISABLE_SCHEDULER", secret),
            ("AUDIT_RETENTION_DAYS", secret),
            ("NOTIFY_TYPE", secret),
            ("ASPNETCORE_ENVIRONMENT", secret),
            ("LOG_LEVEL", secret),
            ("APP_FAKE_NOW", secret));

        var message = FailureMessage(values);

        message.Should().NotContain(secret);
        message.Should().Contain("NOTIFY_URL").And.Contain("PORT").And.Contain("SEED_USERS");
    }

    [Fact]
    public void Validation_validValuesInFailingConfiguration_areNotEchoedEither()
    {
        // MONGO_URL may carry credentials; it must stay out of a failure about another variable.
        var message = FailureMessage(("MONGO_URL", "mongodb://user:hunter2@db/x"), ("PORT", "abc"));

        message.Should().NotContain("hunter2");
    }

    [Fact]
    public void ValidateOnStart_invalidConfiguration_failsTheStartupValidator()
    {
        using var provider = Provider(new Dictionary<string, string?> { ["PORT"] = "abc" });

        var validator = provider.GetRequiredService<IStartupValidator>();

        var thrown = Assert.Throws<OptionsValidationException>(validator.Validate);
        thrown.Message.Should().Contain("MONGO_URL").And.Contain("PORT");
    }

    [Fact]
    public void ValidateOnStart_validConfiguration_passes()
    {
        using var provider = Provider(Base);

        var validator = provider.GetRequiredService<IStartupValidator>();

        validator.Validate();
    }
}
