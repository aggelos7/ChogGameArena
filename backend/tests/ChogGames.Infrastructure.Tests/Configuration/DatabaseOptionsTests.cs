using ChogGames.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Tests.Configuration;

public class DatabaseOptionsTests
{
    private const string ValidConnectionString = "Host=localhost;Port=5432;Database=choggames;Username=choggames;Password=test-only";

    private static ServiceProvider BuildProvider(string? connectionString)
    {
        var settings = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            settings["Database:ConnectionString"] = connectionString;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddInfrastructureOptions(configuration).BuildServiceProvider();
    }

    private static OptionsValidationException StartupFailure(string? connectionString)
    {
        using var provider = BuildProvider(connectionString);
        return Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Valid_settings_bind_and_pass_startup_validation()
    {
        using var provider = BuildProvider(ValidConnectionString);

        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.Equal(ValidConnectionString, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_connection_string_fails_startup_with_clear_message(string? connectionString)
    {
        var error = StartupFailure(connectionString);

        Assert.Contains("Required setting 'Database:ConnectionString' is missing", error.Message);
        Assert.Contains("Database__ConnectionString", error.Message);
        Assert.Contains("dotnet user-secrets set", error.Message);
    }

    [Theory]
    [InlineData("Database=choggames;Username=choggames;Password=secret-value", "Host")]
    [InlineData("Host=localhost;Username=choggames;Password=secret-value", "Database")]
    [InlineData("Host=localhost;Database=choggames;Password=secret-value", "Username")]
    [InlineData("Host=;Database=choggames;Username=choggames;Password=secret-value", "Host")]
    public void Connection_string_without_required_key_fails(string connectionString, string missingKey)
    {
        var error = StartupFailure(connectionString);

        Assert.Contains($"missing required key(s): {missingKey}", error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
    }

    [Fact]
    public void Malformed_connection_string_fails_without_echoing_the_value()
    {
        var error = StartupFailure("Host=localhost;Password='secret-value");

        Assert.Contains("is not a valid connection string", error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
    }
}
