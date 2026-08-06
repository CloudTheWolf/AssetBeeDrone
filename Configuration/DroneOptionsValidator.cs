using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Configuration;

public sealed class DroneOptionsValidator : IValidateOptions<DroneOptions>
{
    public ValidateOptionsResult Validate(string? name, DroneOptions options)
    {
        if (options.CollectionIntervalMinutes is < 1 or > 10080)
        {
            return ValidateOptionsResult.Fail(
                "Drone:CollectionIntervalMinutes must be between 1 and 10080.");
        }

        if (options.RequestTimeoutSeconds is < 5 or > 300)
        {
            return ValidateOptionsResult.Fail(
                "Drone:RequestTimeoutSeconds must be between 5 and 300.");
        }

        if (options.MaxRetryAttempts is < 0 or > 10)
        {
            return ValidateOptionsResult.Fail(
                "Drone:MaxRetryAttempts must be between 0 and 10.");
        }

        bool isHttps = options.Endpoint.Scheme == Uri.UriSchemeHttps;
        bool isDebugLoopbackHttp =
            options.Debug &&
            options.Endpoint.Scheme == Uri.UriSchemeHttp &&
            options.Endpoint.IsLoopback;
        if (!isHttps && !isDebugLoopbackHttp)
        {
            return ValidateOptionsResult.Fail(
                "Drone:Endpoint must use HTTPS. HTTP is allowed only for a loopback " +
                "endpoint when Drone:Debug is enabled.");
        }

        if (!string.IsNullOrWhiteSpace(options.BearerToken) &&
            !string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Fail(
                "Configure either Drone:BearerToken or Drone:ApiKey, not both.");
        }

        if (!string.IsNullOrWhiteSpace(options.Type) &&
            !options.Type.Equals("hardware", StringComparison.OrdinalIgnoreCase) &&
            !options.Type.Equals("virtualware", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Drone:Type must be either hardware or virtualware.");
        }

        if (options.Debug && string.IsNullOrWhiteSpace(options.DebugOutputPath))
        {
            return ValidateOptionsResult.Fail(
                "Drone:DebugOutputPath must be set when Drone:Debug is enabled.");
        }

        return ValidateOptionsResult.Success;
    }
}
