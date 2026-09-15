using Midora.Audio;
using Midora.Common;
using Midora.Persistence;

namespace Midora.Application;

public readonly record struct ApplicationSessionStorageCleanupResult(
    int AudioCacheDirectoryCount,
    int SessionContentDirectoryCount,
    int CompilerRunDirectoryCount,
    int AudioWorkerExchangeDirectoryCount);

public static class ApplicationSessionStorageCleanup
{
    public static ApplicationSessionStorageCleanupResult ClearInactiveSessions(
        ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        int audioCacheDirectories = AudioCacheSessionStore.ClearInactiveSessions(
            preferences.AudioCache.RootPath);
        int sessionContentDirectories =
            SessionContentDirectoryLease.ClearDefaultInactiveDirectories();
        int compilerRunDirectories =
            MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(
                MidoraProgramData.Current.CompilerRunsDirectory);
        int audioWorkerExchangeDirectories =
            MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(
                MidoraProgramData.Current.AudioWorkerExchangeDirectory);
        return new(
            audioCacheDirectories,
            sessionContentDirectories,
            compilerRunDirectories,
            audioWorkerExchangeDirectories);
    }
}
