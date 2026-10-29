using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

/// <summary>App-wide access to the one <see cref="ConfigStore"/> (atomic save, backup, migration).</summary>
public static class ConfigService
{
    /// <summary>
    /// Test hook: points persistence at a scratch file so a harness never edits
    /// the running app's configuration. Null in production.
    /// </summary>
    internal static string? PathOverride
    {
        get => _override;
        set
        {
            _override = value;
            _store = null;
        }
    }

    private static string? _override;
    private static ConfigStore? _store;

    private static ConfigStore Store => _store ??= new ConfigStore(_override ?? ConfigStore.DefaultPath);

    public static string Path => Store.Path;

    public static AppConfig Load()
    {
        var cfg = Store.Load(out bool migrated);
        if (migrated)
            Store.Save(cfg); // persist new roles/IDs at once so they never change again
        return cfg;
    }

    public static bool Save(AppConfig config) => Store.Save(config);
}
