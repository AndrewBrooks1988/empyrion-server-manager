using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EmpyrionManager;

// ============================================================ manager settings (manager-settings.json)

public class MaintenanceSettings
{
    /// <summary>When the restart itself happens (local time). The task starts earlier by the longest warning.</summary>
    public string RestartTime { get; set; } = "04:00";
    public int[] WarnMinutes { get; set; } = [15, 10, 5, 1];
    public string[] DailyDays { get; set; } = ["Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public string[] WeeklyDays { get; set; } = ["Mon"];
    /// <summary>Wipe types for visited playfields in the starter systems (daily). Empty = none.</summary>
    public string DailyStarterWipe { get; set; } = "deposit";
    /// <summary>Wipe types for every visited space playfield (daily). 'poi' respawns asteroids in scenarios where they are POIs.</summary>
    public string DailySpaceWipe { get; set; } = "poi";
    /// <summary>Wipe types for every visited playfield OUTSIDE the starter systems (daily; planets and space). Empty = none.</summary>
    public string DailyOtherWipe { get; set; } = "";
    /// <summary>Wipe types for every visited playfield (weekly).</summary>
    public string WeeklyWipe { get; set; } = "poi deposit terrain";
    /// <summary>Solar system names (from Sectors.yaml) treated as starter systems.</summary>
    public string[] StarterSystems { get; set; } = [];
    public int BackupsToKeep { get; set; } = 14;
    public string[] SkipTypes { get; set; } = ["SunRandom", "SpaceWarpTargetFixed", "GasGiant"];
    public int PlayWarnMinutes { get; set; } = 1;
    /// <summary>Also run the daily maintenance 12 hours after the restart time, every day.</summary>
    public bool TwiceDaily { get; set; } = false;

    [JsonIgnore] public int LeadMinutes => WarnMinutes.Length == 0 ? 0 : WarnMinutes.Max();
}

public class TaskSettings
{
    public string Folder { get; set; } = @"\Empyrion\";
    public string DailyName { get; set; } = "Empyrion Daily Maintenance";
    public string WeeklyName { get; set; } = "Empyrion Weekly Reset";
}

/// <summary>
/// Everything the manager (and the maintenance script) needs to know. Stored as manager-settings.json next to the exe.
/// Nothing server-specific is compiled in; defaults are generic.
/// </summary>
public class ManagerOptions
{
    public string ServerDir { get; set; } = "";
    public string ConfigFile { get; set; } = "dedicated.yaml";
    public string Url { get; set; } = "http://127.0.0.1:8090";
    public string LaunchMode { get; set; } = "-startDedi";
    public string Title { get; set; } = "Empyrion Server Manager";
    public string SteamGameUri { get; set; } = "steam://rungameid/383120";
    public string SteamCmdPath { get; set; } = "";
    /// <summary>PBKDF2 hash ("iterations.salt.hash", base64). Empty = no password (local / tailnet use).</summary>
    public string AccessPasswordHash { get; set; } = "";
    /// <summary>Open the dashboard in the browser when the manager starts (skipped with --no-browser).</summary>
    public bool OpenBrowserOnStart { get; set; } = true;
    /// <summary>Check GitHub for new releases (startup + every 6 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;
    public MaintenanceSettings Maintenance { get; set; } = new();
    public TaskSettings Tasks { get; set; } = new();

    [JsonIgnore] public string AppDir { get; private set; } = AppContext.BaseDirectory;
    [JsonIgnore] public string SettingsPath => Path.Combine(AppDir, "manager-settings.json");
    [JsonIgnore] public string ConfigPath => Path.Combine(ServerDir, ConfigFile);
    [JsonIgnore] public string ScriptPath => Path.Combine(AppDir, "scripts", "Empyrion-Maintenance.ps1");
    [JsonIgnore] public string LauncherPath => Path.Combine(ServerDir, "EmpyrionLauncher.exe");
    [JsonIgnore] public bool ServerDirValid => ServerDir.Length > 0 && File.Exists(LauncherPath);
    [JsonIgnore] public bool ConfigValid => ServerDirValid && File.Exists(ConfigPath);
    [JsonIgnore] public bool Configured => File.Exists(SettingsPath) && ConfigValid;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads manager-settings.json, falling back to the legacy "Manager" section of appsettings.json.</summary>
    public static ManagerOptions Load(string appDir, IConfiguration legacy)
    {
        ManagerOptions o;
        var path = Path.Combine(appDir, "manager-settings.json");
        if (File.Exists(path))
            o = JsonSerializer.Deserialize<ManagerOptions>(File.ReadAllText(path), Json) ?? new();
        else
            o = legacy.GetSection("Manager").Get<ManagerOptions>() ?? new();
        o.AppDir = appDir;
        if (o.ServerDir.Length > 0 && !Path.IsPathRooted(o.ServerDir))
            o.ServerDir = Path.GetFullPath(Path.Combine(appDir, o.ServerDir));
        o.Maintenance ??= new(); o.Tasks ??= new();
        return o;
    }

    public void Save()
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, SettingsPath, true);
    }

    // ---- optional dashboard password
    public void SetPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) { AccessPasswordHash = ""; return; }
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 120_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        AccessPasswordHash = $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool CheckPassword(string password)
    {
        var parts = AccessPasswordHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var it)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string RandomSecret(int length = 20)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return new string(RandomNumberGenerator.GetItems<char>(chars, length));
    }
}

// ============================================================ line-based YAML editing (keeps comments and layout)

/// <summary>
/// Minimal, comment-preserving editor for Empyrion's YAML files. It only ever touches the single line for a key:
/// active "Key: value" lines are updated in place, commented "# Key: value" lines are re-enabled, and missing keys
/// are inserted at the top of their section/block.
/// </summary>
public static class YamlLines
{
    public static string Format(string value, bool raw)
    {
        if (raw) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    public static string StripComment(string v, out string? comment)
    {
        comment = null;
        bool inQuote = false; char q = '\0';
        for (int i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (inQuote) { if (c == q) inQuote = false; }
            else if (c is '"' or '\'') { inQuote = true; q = c; }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(v[i - 1]))) { comment = v[i..].Trim(); return v[..i]; }
        }
        return v;
    }

    public static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v[1..^1].Replace("\\\"", "\"");
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        return v;
    }

    /// <summary>Body line range [start, end) of a top-level section like "ServerConfig:".</summary>
    public static (int Start, int End)? Section(List<string> lines, string section)
    {
        int header = lines.FindIndex(l => Regex.IsMatch(l, $@"^{Regex.Escape(section)}\s*:\s*(#.*)?$"));
        if (header < 0) return null;
        int end = lines.Count;
        for (int i = header + 1; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"^[A-Za-z_][\w]*\s*:")) { end = i; break; }
        return (header + 1, end);
    }

    /// <summary>Reads active key values within a range.</summary>
    public static Dictionary<string, string> Read(List<string> lines, (int Start, int End) range)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = range.Start; i < range.End; i++)
        {
            var m = Regex.Match(lines[i], @"^\s+(?:-\s+)?([A-Za-z_]\w*)\s*:(.*)$");
            if (!m.Success) continue;
            d[m.Groups[1].Value] = Unquote(StripComment(m.Groups[2].Value, out _));
        }
        return d;
    }

    /// <summary>Sets (value != null) or comments out (value == null) a key within a range. Returns true if changed.</summary>
    public static bool Set(List<string> lines, (int Start, int End) range, string key, string? value, bool raw, string defaultIndent = "    ")
    {
        var active = new Regex($@"^(\s+){Regex.Escape(key)}\s*:(.*)$");
        var commented = new Regex($@"^(\s*)#\s*{Regex.Escape(key)}\s*:(.*)$");
        int activeAt = -1, commentedAt = -1;
        for (int i = range.Start; i < range.End; i++)
        {
            if (activeAt < 0 && active.IsMatch(lines[i])) activeAt = i;
            else if (commentedAt < 0 && commented.IsMatch(lines[i])) commentedAt = i;
        }
        string indent = defaultIndent;
        for (int i = range.Start; i < range.End; i++)
        {
            var m = Regex.Match(lines[i], @"^(\s+)[A-Za-z_]\w*\s*:");
            if (m.Success) { indent = m.Groups[1].Value; break; }
        }

        if (value == null)
        {
            if (activeAt < 0) return false;
            var m = active.Match(lines[activeAt]);
            lines[activeAt] = $"{m.Groups[1].Value}# {key}: {StripComment(m.Groups[2].Value, out _).Trim()}";
            return true;
        }

        var formatted = Format(value, raw);
        if (activeAt >= 0)
        {
            var m = active.Match(lines[activeAt]);
            var old = StripComment(m.Groups[2].Value, out var comment);
            if (Unquote(old) == value && old.Trim() == formatted) return false;
            lines[activeAt] = $"{m.Groups[1].Value}{key}: {formatted}" + (comment != null ? "    " + comment : "");
            return true;
        }
        if (commentedAt >= 0)
        {
            StripComment(commented.Match(lines[commentedAt]).Groups[2].Value, out var comment);
            lines[commentedAt] = $"{indent}{key}: {formatted}" + (comment != null ? "    " + comment : "");
            return true;
        }
        lines.Insert(range.Start, $"{indent}{key}: {formatted}");
        return true;
    }

    public static List<string> Load(string path) => File.ReadAllLines(path).ToList();

    public static void Save(string path, List<string> lines)
    {
        File.Copy(path, path + ".bak", true);                       // one-step undo
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}

// ============================================================ field schemas for the settings forms

public record FieldDef(string Key, string Section, string Label, string Type, string? Help = null,
                       string[]? Options = null, bool Secret = false, bool Required = false, string? Group = null);

public static class Schemas
{
    // Dedicated server config (ServerConfig / GameConfig sections)
    public static readonly FieldDef[] Server =
    [
        new("Srv_Name", "ServerConfig", "Server name", "text", "Shown in the server browser.", Group: "Identity", Required: true),
        new("Srv_Description", "ServerConfig", "Description", "text", "Server browser text, max 127 characters. Supports colours like [00ffff]text[-] and \\n for new lines.", Group: "Identity"),
        new("Srv_Password", "ServerConfig", "Join password", "password", "Leave empty for an open server.", Secret: true, Group: "Identity"),
        new("Srv_Public", "ServerConfig", "Listed in server browser", "bool", Group: "Identity"),
        new("Srv_MaxPlayers", "ServerConfig", "Max players", "number", Group: "Players"),
        new("KickPlayerWithPing", "ServerConfig", "Kick players with ping above (ms)", "number", "Empty = off.", Group: "Players"),
        new("DisableSteamFamilySharing", "ServerConfig", "Block Steam Family Sharing", "bool", Group: "Players"),
        new("PlayerLoginParallelCount", "ServerConfig", "Max simultaneous logins", "number", Group: "Players"),
        new("EACActive", "ServerConfig", "Easy Anti-Cheat", "bool", "Players must enable EAC in their options to join.", Group: "Players"),
        new("EnableDLC", "ServerConfig", "Use DLC content", "bool", "Every player then needs the DLC.", Group: "Players"),
        new("Srv_Port", "ServerConfig", "Game port", "number", "Uses this port and the next few. Port-forward these, never the Telnet port.", Group: "Network", Required: true),
        new("Srv_ReservePlayfields", "ServerConfig", "Reserve playfield servers", "number", "Idle playfield processes kept ready (0–10).", Group: "Performance"),
        new("TimeoutBootingPfServer", "ServerConfig", "Playfield boot timeout (s)", "number", Group: "Performance"),
        new("HeartbeatServer", "ServerConfig", "Playfield heartbeat timeout (s)", "number", Group: "Performance"),
        new("HeartbeatClient", "ServerConfig", "Client heartbeat timeout (s)", "number", Group: "Performance"),
        new("Srv_StopPeriod", "ServerConfig", "Auto-stop playfields every (hours)", "number", "Leave empty when the manager's scheduled restarts are used.", Group: "Performance"),
        new("MaxAllowedSizeClass", "ServerConfig", "Max blueprint size class", "number", "0 = unlimited.", Group: "Blueprints"),
        new("AllowedBlueprints", "ServerConfig", "Allowed blueprints", "select", Options: ["All", "StockOnly", "None"], Group: "Blueprints"),
        new("Tel_Enabled", "ServerConfig", "Telnet console", "bool", "Required by the manager.", Group: "Telnet", Required: true),
        new("Tel_Port", "ServerConfig", "Telnet port", "number", "Never port-forward this.", Group: "Telnet"),
        new("Tel_Pwd", "ServerConfig", "Telnet password", "password", "Used by the manager. Never shown here.", Secret: true, Group: "Telnet"),
        new("SaveDirectory", "ServerConfig", "Save folder", "text", "Relative to the server folder.", Group: "Game"),
        new("GameName", "GameConfig", "Save game name", "text", "Changing this starts a NEW save (the old one is kept).", Group: "Game", Required: true),
        new("Mode", "GameConfig", "Mode", "select", Options: ["Survival", "Creative"], Group: "Game"),
        new("Seed", "GameConfig", "World seed", "number", "Only used when a new save is created.", Group: "Game"),
        new("CustomScenario", "GameConfig", "Scenario", "scenario", "Folder in Content\\Scenarios. Only used when a new save is created.", Group: "Game"),
        new("SharedDataURL", "GameConfig", "Shared data download URL", "text", "Optional cloud link to the zipped SharedData folder.", Group: "Game"),
    ];

    static readonly string[] TF = ["true", "false"];

    // gameoptions.yaml (the block matching MP + mode)
    public static readonly FieldDef[] GameOptions =
    [
        new("DecayTime", "", "Decay time (h)", "number", "Structures with no core or fewer than 10 blocks are removed after this long unvisited. 0 = off.", Group: "Structures"),
        new("WipeTime", "", "Wipe time (h)", "number", "ANY player structure is removed after this long unvisited. 0 = off.", Group: "Structures"),
        new("ProtectTime", "", "Offline protection (h)", "number", Group: "Structures"),
        new("ProtectDelay", "", "Offline protection delay (s)", "number", Group: "Structures"),
        new("MaxStructures", "", "Max structures per playfield", "number", Group: "Structures"),
        new("GroundedStructureSpawn", "", "Blueprints must spawn on a base/CV", "select", Options: TF, Group: "Structures"),
        new("UndergroundBpSpawn", "", "Spawn blueprints below terrain", "select", Options: ["Always", "PvE", "Never"], Group: "Structures"),
        new("AntiGriefDistancePvE", "", "Base spacing PvE (m)", "number", Group: "Anti-grief"),
        new("AntiGriefDistancePvP", "", "Base spacing PvP (m)", "number", Group: "Anti-grief"),
        new("AntiGriefOresDistance", "", "No-build zone around deposits (m)", "number", Group: "Anti-grief"),
        new("AntiGriefOresZone", "", "Deposit no-build zone applies on", "select", Options: ["All", "PvP", "PvE"], Group: "Anti-grief"),
        new("EnableVolumeWeight", "", "Mass & volume", "select", Options: TF, Group: "Limits"),
        new("EnableCPUPoints", "", "CPU limits", "select", Options: TF, Group: "Limits"),
        new("EnableMaxBlockCount", "", "Block limits", "select", Options: TF, Group: "Limits"),
        new("EnableUnlockCheckInBPF", "", "Blueprint factory checks unlocks", "select", Options: TF, Group: "Limits"),
        new("AutoMinerDepletion", "", "Auto miners deplete deposits", "select", Options: TF, Group: "Limits"),
        new("TurretUndergroundCheck", "", "Disable turrets below terrain", "select", Options: TF, Group: "Limits"),
        new("ThrustersNeedOpenSpace", "", "Thrusters need open space", "select", Options: TF, Group: "Limits"),
        new("EnableTrading", "", "Trading", "select", Options: ["All", "None", "Player2Player", "Player2System", "Local", "Global", "GlobalEverywhere"], Group: "World"),
        new("RegeneratePOIs", "", "POIs regenerate on their own timers", "select", Options: TF, Help: "Timers only run while a playfield stays loaded; scheduled 'poi' wipes are more reliable.", Group: "World"),
        new("MaxSpawnedEnemies", "", "Max enemies around a player", "number", Group: "World"),
        new("ForcePvP", "", "Force all playfields PvP", "select", Options: TF, Group: "World"),
        new("FriendlyFireInPvP", "", "Friendly fire in PvP", "select", Options: TF, Group: "World"),
        new("FOWTransparency", "", "Fog-of-war transparency (0–100)", "number", Group: "World"),
        new("EnableDecoKnockDown", "", "Decorations can be knocked down", "select", Options: TF, Group: "World"),
        new("DespawnEscapePod", "", "Escape pod despawns", "select", Options: TF, Group: "Players"),
        new("OriginDefault", "", "Default origin", "text", Group: "Players"),
        new("OriginFactionStart", "", "New players join a shared origin faction", "select", Options: TF, Group: "Players"),
        new("OriginAccessOthers", "", "Alliances across origins", "select", Options: TF, Group: "Players"),
        new("OriginAutoAlliance", "", "Same-origin factions auto-allied", "select", Options: TF, Group: "Players"),
        new("DiffEscapePodContent", "", "Escape pod content", "select", Options: ["Easy", "Medium", "Hard"], Group: "Difficulty"),
        new("DiffPlayerProgression", "", "XP progression", "select", Options: ["Faster", "Normal", "Slower"], Group: "Difficulty"),
        new("DiffDegradationSpeed", "", "Tool/weapon degradation", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffPlayerBackpackDrop", "", "On death", "select", Options: ["DropNothing", "DropBagOnly", "DropEverything"], Group: "Difficulty"),
        new("DiffSpawnLocal", "", "Respawn at death location", "select", Options: ["Always", "PvP", "PvE", "Never"], Group: "Difficulty"),
        new("DiffFoodConsumption", "", "Food consumption", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffOxygenConsumption", "", "Oxygen consumption", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffRadiationTemperature", "", "Radiation & temperature", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffAmountOfOre", "", "Ore per deposit", "select", Options: ["Poor", "Normal", "Rich"], Group: "Difficulty"),
        new("DiffNumberOfDeposits", "", "Number of deposits", "select", Options: ["Few", "Normal", "Plenty"], Group: "Difficulty"),
        new("DiffAttackStrength", "", "Enemy strength", "select", Options: ["Easy", "Medium", "Hard"], Group: "Difficulty"),
        new("DiffDronePresence", "", "Drone presence", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffDroneBaseAttack", "", "Drone base attacks", "select", Options: ["Low", "Normal", "High", "Off"], Group: "Difficulty"),
        new("DiffConstrCraftTime", "", "Constructor speed", "select", Options: ["Instant", "Faster", "Normal", "Slower"], Group: "Difficulty"),
        new("DiffBpProdTime", "", "Blueprint factory speed", "select", Options: ["Instant", "Faster", "Normal", "Slower"], Group: "Difficulty"),
    ];

    public static bool IsRaw(FieldDef f) => f.Type is "number" or "bool" or "select";
}
