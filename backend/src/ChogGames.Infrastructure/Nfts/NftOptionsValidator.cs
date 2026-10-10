using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts;

public sealed class NftOptionsValidator(IHostEnvironment environment) : IValidateOptions<NftOptions>
{
    private const string Section = NftOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, NftOptions options)
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(options.Provider))
        {
            errors.Add($"'{Section}:Provider' must be one of: {string.Join(", ", Enum.GetNames<NftProviderKind>())}.");
        }
        else if (options.Provider == NftProviderKind.Mock && environment.IsProduction())
        {
            errors.Add($"'{Section}:Provider' 'Mock' reports fake ownership and is not allowed in Production.");
        }

        if (!Enum.IsDefined(options.Mock.OtherWallets))
        {
            errors.Add($"'{Section}:Mock:OtherWallets' must be one of: {string.Join(", ", Enum.GetNames<MockNftHolding>())}.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
