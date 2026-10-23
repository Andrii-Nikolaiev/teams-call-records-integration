using Microsoft.Extensions.Options;

namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Adapts an options class's Validate() to IValidateOptions (TRP 3.18.1).</summary>
internal sealed class DelegateOptionsValidator<T> : IValidateOptions<T> where T : class
{
    private readonly Func<T, IEnumerable<string>> _validate;

    public DelegateOptionsValidator(Func<T, IEnumerable<string>> validate) => _validate = validate;

    public ValidateOptionsResult Validate(string? name, T options)
    {
        var errors = _validate(options).ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
