namespace Searchlight.Abstractions;

public sealed record StartupRegistrationState(bool IsEnabled, bool CanChange, string Message = "");

/// <summary>Per-installation OS startup state, deliberately separate from shared app settings.</summary>
public interface IStartupRegistration
{
    Task<StartupRegistrationState> GetStateAsync();
    Task<StartupRegistrationState> SetEnabledAsync(bool enabled);
}
