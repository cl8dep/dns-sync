using DnsSync.Config;

namespace DnsSync.Validation;

public static class ConfigValidator
{
    public static ValidationResult Validate(DnsSyncConfig config)
    {
        var result = new ValidationResult();

        var errors = ConfigLoader.ValidateStructure(config);
        foreach (var error in errors)
            result.AddError(error);

        return result;
    }
}
