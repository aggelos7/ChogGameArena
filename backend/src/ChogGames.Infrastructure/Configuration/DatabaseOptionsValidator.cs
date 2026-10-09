using System.Data.Common;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Configuration;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    private const string Setting = $"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.ConnectionString)}";
    private const string Hint =
        $"Set it with 'dotnet user-secrets set \"{Setting}\" \"<value>\"' (project ChogGames.Api) " +
        $"or the environment variable '{DatabaseOptions.SectionName}__{nameof(DatabaseOptions.ConnectionString)}'.";

    private static readonly string[] RequiredKeys = ["Host", "Database", "Username"];

    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail($"Required setting '{Setting}' is missing. {Hint}");
        }

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = options.ConnectionString;
        }
        catch (ArgumentException)
        {
            // Never echo the value: it may contain a password.
            return ValidateOptionsResult.Fail($"Setting '{Setting}' is not a valid connection string. {Hint}");
        }

        var missing = RequiredKeys
            .Where(key => !builder.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
            .ToArray();

        return missing.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Setting '{Setting}' is missing required key(s): {string.Join(", ", missing)}. {Hint}");
    }
}
