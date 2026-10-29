using System.Text.RegularExpressions;

namespace WebcamSwitcher.Core;

/// <summary>One problem found by <see cref="CameraRoles.Validate"/>.</summary>
public sealed record RoleIssue(string Role, string Field, string Message);

/// <summary>
/// Rules for Camera Roles: the standard <c>CAM-NN</c> naming, uniqueness of
/// role / virtual camera name / virtual camera ID / device ID, and migration of
/// schema 1 configs. There is no fixed maximum number of roles.
/// </summary>
public static partial class CameraRoles
{
    public const string RolePrefix = "CAM-";

    /// <summary>Roles may be any short identifier; <c>CAM-01</c> is the standard form.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,31}$")]
    private static partial Regex RoleRegex();

    [GeneratedRegex(@"^CAM-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandardRoleRegex();

    /// <summary>Standard role for a 1-based number: 1 -> CAM-01, 100 -> CAM-100.</summary>
    public static string StandardRole(int number) => $"{RolePrefix}{number:00}";

    /// <summary>Number of a standard role, or null for a custom one.</summary>
    public static int? StandardNumber(string? role)
    {
        var m = StandardRoleRegex().Match(role ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }

    /// <summary>Lowest standard role not used by <paramref name="cameras"/>.</summary>
    public static string NextFreeRole(IEnumerable<CameraConfig> cameras)
    {
        var used = new HashSet<string>(cameras.Select(c => c.Role), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string r = StandardRole(n);
            if (!used.Contains(r))
                return r;
        }
    }

    public static string NewVirtualCameraId() => Guid.NewGuid().ToString("N");

    public static bool IsValidRole(string? role) => role != null && RoleRegex().IsMatch(role);

    /// <summary>
    /// A virtual camera name is written to the tab-separated vcams.ini and handed
    /// to MFCreateVirtualCamera, so it must be printable, short and tab-free.
    /// </summary>
    public static bool IsValidVirtualCameraName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.Trim() == name &&
        !name.Any(ch => char.IsControl(ch)) &&
        !string.Equals(name, VirtualCameraService.SwitcherName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a new, unassigned role entry with a fresh persistent ID.</summary>
    public static CameraConfig CreateRole(string role) => new()
    {
        Role = role,
        VirtualCameraName = role,
        VirtualCameraId = NewVirtualCameraId(),
    };

    /// <summary>
    /// Every uniqueness/format violation in the role list. An empty list means
    /// the configuration may be started as is. Comparison is case-insensitive
    /// because Windows device names and pipe names are.
    /// </summary>
    public static List<RoleIssue> Validate(IReadOnlyList<CameraConfig> cameras)
    {
        var issues = new List<RoleIssue>();
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in cameras)
        {
            string role = c.Role ?? "";
            if (!IsValidRole(role))
                issues.Add(new RoleIssue(role, nameof(c.Role), $"Invalid Camera Role '{role}' (use e.g. CAM-01)."));
            else if (!roles.TryAdd(role, role))
                issues.Add(new RoleIssue(role, nameof(c.Role), $"Camera Role '{role}' is used more than once."));

            if (!IsValidVirtualCameraName(c.VirtualCameraName))
                issues.Add(new RoleIssue(role, nameof(c.VirtualCameraName), $"Invalid Virtual Camera Name '{c.VirtualCameraName}'."));
            else if (!names.TryAdd(c.VirtualCameraName, role))
                issues.Add(new RoleIssue(role, nameof(c.VirtualCameraName),
                    $"Virtual Camera Name '{c.VirtualCameraName}' is already used by {names[c.VirtualCameraName]}."));

            if (string.IsNullOrWhiteSpace(c.VirtualCameraId) || !Guid.TryParse(c.VirtualCameraId, out _))
                issues.Add(new RoleIssue(role, nameof(c.VirtualCameraId), $"Missing or invalid Virtual Camera ID for {role}."));
            else if (!ids.TryAdd(c.VirtualCameraId, role))
                issues.Add(new RoleIssue(role, nameof(c.VirtualCameraId),
                    $"Virtual Camera ID of {role} is already used by {ids[c.VirtualCameraId]}."));

            if (c.IsAssigned && !devices.TryAdd(c.DeviceId!, role))
                issues.Add(new RoleIssue(role, nameof(c.DeviceId),
                    $"Device ID is already assigned to {devices[c.DeviceId!]}; one physical camera can feed only one role."));
        }
        return issues;
    }

    /// <summary>Roles that take part in any duplicate (they must not be started).</summary>
    public static HashSet<string> DuplicatedRoles(IReadOnlyList<CameraConfig> cameras)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Mark(Func<CameraConfig, string?> key)
        {
            foreach (var g in cameras.Where(c => !string.IsNullOrWhiteSpace(key(c)))
                                     .GroupBy(c => key(c)!, StringComparer.OrdinalIgnoreCase)
                                     .Where(g => g.Count() > 1))
                foreach (var c in g)
                    result.Add(c.Role ?? "");
        }
        Mark(c => c.Role);
        Mark(c => c.VirtualCameraName);
        Mark(c => c.VirtualCameraId);
        Mark(c => c.DeviceId);
        return result;
    }

    /// <summary>
    /// Brings a loaded config up to the current schema. Schema 1 entries are
    /// given roles CAM-01.. in their saved order (that order came from the
    /// user's saved list, not from device enumeration), a virtual camera name
    /// equal to the role and a fresh persistent ID. Fields that are merely
    /// missing on a schema 2 file are filled the same way, so a hand-edited
    /// file without IDs still starts. Existing roles/IDs are never changed.
    /// Returns a human-readable list of what was done (for the log).
    /// </summary>
    public static List<string> Migrate(AppConfig config)
    {
        var notes = new List<string>();
        config.Cameras ??= new();
        int from = config.SchemaVersion;

        if (from < 2)
        {
            notes.Add($"migrating config schema {from} -> {AppConfig.CurrentSchemaVersion}");
            if (config.SwitcherVirtualCameraEnabled)
            {
                // This build centres on per-role cameras; the switcher starts off.
                config.SwitcherVirtualCameraEnabled = false;
                notes.Add("switcher virtual camera disabled (opt-in in this version)");
            }
        }

        foreach (var c in config.Cameras)
        {
            if (string.IsNullOrWhiteSpace(c.PhysicalFriendlyName) && !string.IsNullOrWhiteSpace(c.FriendlyName))
            {
                c.PhysicalFriendlyName = c.FriendlyName!;
                notes.Add($"FriendlyName '{c.FriendlyName}' -> PhysicalFriendlyName");
            }
            c.FriendlyName = null; // legacy field: never written again
        }

        foreach (var c in config.Cameras)
        {
            if (string.IsNullOrWhiteSpace(c.Role))
            {
                c.Role = NextFreeRole(config.Cameras);
                notes.Add($"assigned role {c.Role} to '{c.DisplayDeviceName}' ({DeviceIdTail(c.DeviceId)})");
            }
            if (string.IsNullOrWhiteSpace(c.VirtualCameraName))
            {
                c.VirtualCameraName = c.Role;
                notes.Add($"{c.Role}: virtual camera name '{c.VirtualCameraName}'");
            }
            if (string.IsNullOrWhiteSpace(c.VirtualCameraId))
            {
                c.VirtualCameraId = NewVirtualCameraId();
                notes.Add($"{c.Role}: new virtual camera id {c.VirtualCameraId}");
            }
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        return notes;
    }

    /// <summary>
    /// Short, distinguishing tail of a device ID: the USB instance part, which
    /// is what differs between two identical cameras, e.g.
    /// <c>…#6&amp;8EC54CD&amp;0&amp;0000</c>.
    /// </summary>
    public static string DeviceIdTail(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "(none)";
        // \\?\USB#VID_0C45&PID_6D1E&MI_00#6&8ec54cd&0&0000#{e5323777-...}\GLOBAL
        var parts = deviceId.Split('#');
        if (parts.Length >= 3)
            return parts[1] + " #" + parts[2];
        return deviceId.Length <= 32 ? deviceId : "…" + deviceId[^32..];
    }
}
