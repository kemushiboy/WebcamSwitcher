using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebcamSwitcher.Core;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> without ever leaving a half-written
/// file behind:
/// <list type="bullet">
/// <item>save writes <c>config.json.tmp</c>, flushes it to disk, then swaps it in
/// with <see cref="File.Replace(string,string,string)"/>, which also keeps the
/// previous file as <c>config.json.bak</c>;</item>
/// <item>load falls back to the backup when the main file is unreadable, and
/// never overwrites a broken file (it is copied aside as <c>.corrupt-*</c>);</item>
/// <item>schema 1 files (no <c>SchemaVersion</c>) are migrated, and unknown
/// properties survive the round trip via <c>[JsonExtensionData]</c>.</item>
/// </list>
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();

    public ConfigStore(string path) => Path = path;

    public string Path { get; }
    public string BackupPath => Path + ".bak";
    public string TempPath => Path + ".tmp";

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WebcamSwitcher", "config.json");

    /// <summary>
    /// Reads the config. Never throws; returns a fresh default config when
    /// nothing usable exists. <paramref name="migrated"/> is true when the file
    /// needed migration (the caller should save it once so the IDs persist).
    /// </summary>
    public AppConfig Load(out bool migrated)
    {
        migrated = false;
        lock (_lock)
        {
            foreach (var candidate in new[] { Path, BackupPath })
            {
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    var cfg = Parse(File.ReadAllText(candidate, Encoding.UTF8), out int schema);
                    var notes = CameraRoles.Migrate(cfg);
                    foreach (var n in notes)
                        AppLog.Write("Config", n);
                    migrated = notes.Count > 0 || candidate != Path;
                    AppLog.Write("Config", $"loaded '{candidate}' (schema {schema}, {cfg.Cameras.Count} role(s))" +
                                           (candidate != Path ? " FROM BACKUP" : ""));
                    return cfg;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Config", $"cannot read '{candidate}': {ex.GetType().Name}: {ex.Message}");
                    PreserveCorrupt(candidate);
                }
            }
        }

        AppLog.Write("Config", $"no usable config at '{Path}'; starting with defaults");
        var fresh = new AppConfig();
        CameraRoles.Migrate(fresh);
        return fresh;
    }

    public AppConfig Load() => Load(out _);

    /// <summary>Parses JSON text into a config, detecting the schema version.</summary>
    public static AppConfig Parse(string json, out int schemaVersion)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new JsonException("config root is not an object");

        // Schema 1 had no SchemaVersion property; the C# default (current
        // version) would otherwise hide that the file needs migration.
        schemaVersion = node.TryGetPropertyValue("SchemaVersion", out var v) && v is JsonValue jv &&
                        jv.TryGetValue(out int sv) ? sv : 1;

        var cfg = node.Deserialize<AppConfig>(ReadOptions) ?? throw new JsonException("config deserialized to null");
        cfg.SchemaVersion = schemaVersion;
        return cfg;
    }

    /// <summary>
    /// Atomically replaces the config file. Returns false (and logs) on failure;
    /// the previous file is then untouched.
    /// </summary>
    public bool Save(AppConfig config)
    {
        lock (_lock)
        {
            try
            {
                config.SchemaVersion = AppConfig.CurrentSchemaVersion;
                string json = JsonSerializer.Serialize(config, WriteOptions);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(Path))
                    File.Replace(TempPath, Path, BackupPath, ignoreMetadataErrors: true);
                else
                    File.Move(TempPath, Path);

                AppLog.Write("Config", $"saved '{Path}' ({config.Cameras.Count} role(s))");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("Config", $"save FAILED: {ex.GetType().Name}: {ex.Message}");
                try { File.Delete(TempPath); } catch { }
                return false;
            }
        }
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            string aside = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, aside, overwrite: false);
            AppLog.Write("Config", $"kept unreadable file as '{aside}'");
        }
        catch { }
    }
}
