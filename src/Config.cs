using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EmpyrionManager;

public record FieldValue(FieldDef Def, string? Value, bool IsSet);
public record SolarSystem(string Name, string? StarClass, bool LooksLikeStarter);
public record AdminEntry(string SteamId, int Permission, string? Note);

// ============================================================ dedicated server config (.yaml) editor

public class ServerConfigService(ManagerOptions o)
{
    public List<FieldValue> Get()
    {
        if (!File.Exists(o.ConfigPath)) return [];
        var lines = YamlLines.Load(o.ConfigPath);
        var sections = new Dictionary<string, Dictionary<string, string>>();
        foreach (var s in Schemas.Server.Select(f => f.Section).Distinct())
            sections[s] = YamlLines.Section(lines, s) is { } r ? YamlLines.Read(lines, r) : new(StringComparer.OrdinalIgnoreCase);
        return Schemas.Server.Select(f =>
        {
            sections[f.Section].TryGetValue(f.Key, out var v);
            var isSet = !string.IsNullOrEmpty(v);
            return new FieldValue(f, f.Secret ? null : v, isSet);
        }).ToList();
    }

    /// <summary>
    /// Applies edits. A null/empty value comments the key out (unless required). Secret fields: null = keep as is.
    /// Returns the keys that changed.
    /// </summary>
    public List<string> Save(Dictionary<string, string?> values)
    {
        if (!File.Exists(o.ConfigPath)) throw new InvalidOperationException("The server config file doesn't exist.");
        var lines = YamlLines.Load(o.ConfigPath);
        var changed = new List<string>();
        foreach (var (key, raw) in values)
        {
            var f = Schemas.Server.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown setting {key}.");
            if (f.Secret && raw == null) continue;                     // keep existing secret
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (f.Required) throw new ArgumentException($"{f.Label} can't be empty.");
                value = null;
            }
            else if (f.Type == "number" && !long.TryParse(value, out _)) throw new ArgumentException($"{f.Label} must be a whole number.");
            else if (f.Type == "bool") value = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            else if (f.Type == "select" && f.Options != null && !f.Options.Contains(value)) throw new ArgumentException($"{f.Label}: pick one of {string.Join(", ", f.Options)}.");
            if (f.Key == "Srv_Description" && value is { Length: > 127 }) throw new ArgumentException("Description is limited to 127 characters.");

            var range = EnsureSection(lines, f.Section);
            if (YamlLines.Set(lines, range, f.Key, value, Schemas.IsRaw(f))) changed.Add(f.Key);
        }
        if (changed.Count > 0) YamlLines.Save(o.ConfigPath, lines);
        return changed;
    }

    public void SetRaw(string section, string key, string? value, bool raw)
    {
        var lines = YamlLines.Load(o.ConfigPath);
        var range = EnsureSection(lines, section);
        if (YamlLines.Set(lines, range, key, value, raw)) YamlLines.Save(o.ConfigPath, lines);
    }

    static (int, int) EnsureSection(List<string> lines, string section)
    {
        if (YamlLines.Section(lines, section) is { } r) return r;
        lines.Add(""); lines.Add($"{section}:");
        return YamlLines.Section(lines, section)!.Value;
    }

    /// <summary>Turns on Telnet (needed by the manager), generating a password if there isn't one.</summary>
    public void EnableTelnet(ServerFiles files)
    {
        var lines = YamlLines.Load(o.ConfigPath);
        var range = EnsureSection(lines, "ServerConfig");
        YamlLines.Set(lines, range, "Tel_Enabled", "true", true);
        range = YamlLines.Section(lines, "ServerConfig")!.Value;
        var current = YamlLines.Read(lines, range);
        if (!current.TryGetValue("Tel_Port", out var port) || port.Length == 0) YamlLines.Set(lines, range, "Tel_Port", "30004", true);
        range = YamlLines.Section(lines, "ServerConfig")!.Value;
        if (!current.TryGetValue("Tel_Pwd", out var pwd) || pwd.Length == 0) YamlLines.Set(lines, range, "Tel_Pwd", ManagerOptions.RandomSecret(), false);
        YamlLines.Save(o.ConfigPath, lines);
    }

    public static string NewConfigTemplate() => $"""
        ### Dedicated server settings - created by Empyrion Server Manager
        ### Edit in the manager (Settings -> Server config) or by hand. Changes apply when the server (re)starts.

        ServerConfig:
            Srv_Port: 30000
            Srv_Name: "My Empyrion Server"
            # Srv_Password:
            Srv_MaxPlayers: 8
            Srv_Public: true
            # Srv_Description: ""
            # Srv_ReservePlayfields: 1
            # Srv_StopPeriod: 48
            ### Telnet is used by the manager. Never port-forward the Telnet port.
            Tel_Enabled: true
            Tel_Port: 30004
            Tel_Pwd: "{ManagerOptions.RandomSecret()}"
            EACActive: false
            SaveDirectory: Saves
            MaxAllowedSizeClass: 10
            AllowedBlueprints: All
            HeartbeatServer: 15
            # HeartbeatClient: 30
            # KickPlayerWithPing: 300
            TimeoutBootingPfServer: 90
            # PlayerLoginParallelCount: 5
            # PlayerLoginVipNames: ""
            # DisableSteamFamilySharing: true
            # EnableDLC: true

        GameConfig:
            GameName: "MyGame"
            Mode: Survival
            Seed: {Random.Shared.Next(1000000, 9999999)}
            CustomScenario: Default Multiplayer
            # SharedDataURL:
        """;
}

// ============================================================ gameoptions.yaml (rules for the active save)

public class GameOptionsService(ManagerOptions o, ServerFiles files)
{
    /// <summary>The save's own gameoptions.yaml once the save exists, otherwise the scenario's (used to create it).</summary>
    public (string? Path, bool IsSave) File()
    {
        var save = Path.Combine(files.GameDir, "gameoptions.yaml");
        if (System.IO.File.Exists(save)) return (save, true);
        var scen = files.Get("CustomScenario");
        if (scen.Length > 0)
        {
            var p = Path.Combine(o.ServerDir, "Content", "Scenarios", scen, "gameoptions.yaml");
            if (System.IO.File.Exists(p)) return (p, false);
        }
        return (null, false);
    }

    string Mode => files.Get("Mode", "Survival");

    (int Start, int End)? Block(List<string> lines)
    {
        int header = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*-\s*ValidFor:\s*\[\s*MP\s*,\s*{Regex.Escape(Mode)}\s*\]", RegexOptions.IgnoreCase));
        if (header < 0) header = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*-\s*ValidFor:\s*\[[^\]]*\bMP\b", RegexOptions.IgnoreCase));
        if (header < 0) return null;
        int end = lines.Count;
        for (int i = header + 1; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"^\s*-\s*ValidFor:")) { end = i; break; }
        return (header + 1, end);
    }

    public (string? Path, bool IsSave, string Mode, List<FieldValue> Fields, string? Problem) Get()
    {
        var (path, isSave) = File();
        if (path == null) return (null, false, Mode, [], "No gameoptions.yaml found for this save or its scenario. The scenario's defaults apply.");
        var lines = YamlLines.Load(path);
        if (Block(lines) is not { } block) return (path, isSave, Mode, [], $"No 'ValidFor: [ MP, {Mode} ]' block in {Path.GetFileName(path)}.");
        var values = YamlLines.Read(lines, block);
        var fields = Schemas.GameOptions.Select(f =>
        {
            values.TryGetValue(f.Key, out var v);
            return new FieldValue(f, v, !string.IsNullOrEmpty(v));
        }).ToList();
        return (path, isSave, Mode, fields, null);
    }

    public List<string> Save(Dictionary<string, string?> values)
    {
        var (path, _) = File();
        if (path == null) throw new InvalidOperationException("No gameoptions.yaml to edit.");
        var lines = YamlLines.Load(path);
        var changed = new List<string>();
        foreach (var (key, raw) in values)
        {
            var f = Schemas.GameOptions.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown option {key}.");
            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (value != null && f.Type == "number" && !long.TryParse(value, out _)) throw new ArgumentException($"{f.Label} must be a whole number.");
            if (value != null && f.Options != null && !f.Options.Contains(value, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"{f.Label}: pick one of {string.Join(", ", f.Options)}.");
            var block = Block(lines) ?? throw new InvalidOperationException("Multiplayer block not found.");
            if (YamlLines.Set(lines, block, f.Key, value, Schemas.IsRaw(f))) changed.Add(f.Key);
        }
        if (changed.Count > 0) YamlLines.Save(path, lines);
        return changed;
    }
}

// ============================================================ admins (Saves\adminconfig.yaml) + login priority

public class AdminService(ServerFiles files, ServerConfigService config)
{
    public string Path => System.IO.Path.Combine(files.SaveDir, "adminconfig.yaml");
    public static string RoleName(int p) => p switch { 9 => "admin", 6 => "moderator", 3 => "gamemaster", _ => "player" };

    public List<AdminEntry> Read()
    {
        if (!File.Exists(Path)) return [];
        var list = new List<AdminEntry>();
        bool inElevated = false;
        string? id = null, note = null;
        foreach (var raw in File.ReadAllLines(Path))
        {
            if (Regex.IsMatch(raw, @"^[A-Za-z_]\w*\s*:")) { inElevated = raw.StartsWith("Elevated"); continue; }
            if (!inElevated) continue;
            var m = Regex.Match(raw, @"^\s*-\s*Id:\s*(\d+)\s*(?:#\s*(.*))?$");
            if (m.Success) { id = m.Groups[1].Value; note = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null; continue; }
            m = Regex.Match(raw, @"^\s+Permission:\s*(\d+)");
            if (m.Success && id != null) { list.Add(new AdminEntry(id, int.Parse(m.Groups[1].Value), note)); id = null; }
        }
        return list;
    }

    /// <summary>Adds, changes or (permission 0) removes one player's entry, keeping everything else.</summary>
    public void SetRole(string steamId, int permission, string? note)
    {
        var list = Read();
        var existing = list.FirstOrDefault(a => a.SteamId == steamId);
        list.RemoveAll(a => a.SteamId == steamId);
        if (permission > 0) list.Add(new AdminEntry(steamId, permission, existing?.Note ?? note));
        Save(list, ReadPriority());
    }

    public List<string> ReadPriority() =>
        files.Get("PlayerLoginVipNames").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Rewrites the Elevated list (keeping header comments and the Banned section) and the login priority list.</summary>
    public void Save(List<AdminEntry> admins, List<string> priority)
    {
        foreach (var a in admins)
        {
            if (!Regex.IsMatch(a.SteamId, @"^\d{17}$")) throw new ArgumentException($"'{a.SteamId}' isn't a 17-digit SteamID64.");
            if (a.Permission is not (3 or 6 or 9)) throw new ArgumentException("Permission must be 3 (GameMaster), 6 (Moderator) or 9 (Admin).");
        }
        foreach (var p in priority)
            if (!Regex.IsMatch(p, @"^\d{17}$")) throw new ArgumentException($"'{p}' isn't a 17-digit SteamID64.");

        var header = new List<string>();
        var banned = new List<string>();
        if (File.Exists(Path))
        {
            string? section = null;
            foreach (var raw in File.ReadAllLines(Path))
            {
                if (Regex.IsMatch(raw, @"^[A-Za-z_]\w*\s*:")) section = raw.Split(':')[0].Trim();
                if (section == null) header.Add(raw);
                else if (section == "Banned") banned.Add(raw);
            }
        }
        if (header.Count == 0)
            header.AddRange(["---", "# Admin configuration - managed by Empyrion Server Manager",
                             "# 'Permission': 3 = GameMaster, 6 = Moderator, 9 = Admin", ""]);
        var output = new List<string>(header) { "Elevated:" };
        foreach (var a in admins)
        {
            output.Add($"- Id: {a.SteamId}" + (string.IsNullOrWhiteSpace(a.Note) ? "" : $"   # {a.Note!.Replace("\n", " ").Trim()}"));
            output.Add($"  Permission: {a.Permission}");
        }
        output.AddRange(banned);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
        File.WriteAllLines(Path, output, new System.Text.UTF8Encoding(false));

        config.SetRaw("ServerConfig", "PlayerLoginVipNames", priority.Count == 0 ? null : string.Join(",", priority.Distinct()), false);
    }
}

// ============================================================ scenario / galaxy info

public class ScenarioService(ManagerOptions o, ServerFiles files)
{
    public List<string> Scenarios()
    {
        var dir = Path.Combine(o.ServerDir, "Content", "Scenarios");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList() : [];
    }

    public string? SectorsFile()
    {
        var save = Path.Combine(files.GameDir, "Sectors", "Sectors.yaml");
        if (File.Exists(save)) return save;
        var scen = files.Get("CustomScenario");
        var p = Path.Combine(o.ServerDir, "Content", "Scenarios", scen, "Sectors", "Sectors.yaml");
        return scen.Length > 0 && File.Exists(p) ? p : null;
    }

    public List<SolarSystem> SolarSystems()
    {
        var path = SectorsFile();
        if (path == null) return [];
        var list = new List<SolarSystem>();
        string? name = null;
        foreach (var raw in File.ReadLines(path))
        {
            if (raw.TrimStart().StartsWith('#')) continue;
            var m = Regex.Match(raw, @"^\s{2}-\s*Name:\s*(.+?)\s*$");
            if (m.Success) { if (name != null) list.Add(new(name, null, false)); name = m.Groups[1].Value.Trim('\'', '"'); continue; }
            m = Regex.Match(raw, @"^\s+StarClass:\s*(\S+)");
            if (m.Success && name != null)
            {
                var sc = m.Groups[1].Value;
                list.Add(new(name, sc, sc.Contains("Start", StringComparison.OrdinalIgnoreCase)));
                name = null;
            }
        }
        if (name != null) list.Add(new(name, null, false));
        return list.GroupBy(s => s.Name).Select(g => g.First()).ToList();
    }
}

// ============================================================ setup helpers (server folder, config files, SteamCMD)

public static class Setup
{
    public static List<string> ConfigFiles(string serverDir)
    {
        if (!Directory.Exists(serverDir)) return [];
        return Directory.GetFiles(serverDir, "*.yaml")
            .Where(f => { try { return File.ReadAllText(f).Contains("ServerConfig:"); } catch { return false; } })
            .Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList();
    }

    /// <summary>Looks for Empyrion dedicated server installs in the usual places.</summary>
    public static List<string> DetectServerDirs(string appDir)
    {
        var candidates = new List<string>();
        void Probe(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            if (File.Exists(Path.Combine(dir, "EmpyrionLauncher.exe")) && Directory.Exists(Path.Combine(dir, "DedicatedServer")))
                candidates.Add(Path.GetFullPath(dir));
        }
        // folders near the manager (e.g. <root>\Manager\app -> <root>\<server>)
        foreach (var up in new[] { "..", @"..\..", @"..\..\.." })
        {
            var root = Path.GetFullPath(Path.Combine(appDir, up));
            Probe(root);
            try { foreach (var d in Directory.GetDirectories(root)) Probe(d); } catch { }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            foreach (var rel in new[] { @"GameServers", @"Games", @"SteamCMD\steamapps\common", @"steamcmd\steamapps\common",
                                        @"Program Files (x86)\Steam\steamapps\common", @"SteamLibrary\steamapps\common" })
            {
                var root = Path.Combine(drive.RootDirectory.FullName, rel);
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        Probe(d);
                        try { foreach (var dd in Directory.GetDirectories(d)) Probe(dd); } catch { }
                    }
                }
                catch { }
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? DetectSteamCmd(string serverDir)
    {
        var probes = new List<string>();
        if (serverDir.Length > 0)
        {
            var d = new DirectoryInfo(serverDir);
            for (int i = 0; i < 3 && d != null; i++, d = d.Parent)
            {
                probes.Add(Path.Combine(d.FullName, "steamcmd", "steamcmd.exe"));
                probes.Add(Path.Combine(d.FullName, "steamcmd.exe"));
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            probes.Add(Path.Combine(drive.RootDirectory.FullName, "steamcmd", "steamcmd.exe"));
            probes.Add(Path.Combine(drive.RootDirectory.FullName, "SteamCMD", "steamcmd.exe"));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            probes.Add(Path.Combine(dir.Trim(), "steamcmd.exe"));
        return probes.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs SteamCMD to install/update the dedicated server (app 530870), streaming output into the job.</summary>
    public static async Task UpdateServerAsync(string steamCmd, string serverDir, Job job)
    {
        job.Add($"Running SteamCMD: app_update 530870 validate -> {serverDir}");
        job.Add("Note: 'validate' restores the stock server files (e.g. dedicated.yaml) but leaves your saves and custom config files alone.");
        var psi = new ProcessStartInfo(steamCmd,
            $"+force_install_dir \"{serverDir}\" +login anonymous +app_update 530870 validate +quit")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start SteamCMD.");
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) job.Add(e.Data.Trim()); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) job.Add("! " + e.Data.Trim()); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        // SteamCMD's exit codes are unreliable (7 is common on success); judge by its output instead
        job.Add($"SteamCMD finished (exit code {p.ExitCode}). Check above for \"Success! App '530870' fully installed\".");
    }
}

// ============================================================ Steam names for SteamIDs (admins, roles)

/// <summary>
/// Resolves SteamID64 -> a readable name: first from players seen on this server (in-game name),
/// then from the public Steam community profile (persona name), cached for a week in Logs\Manager\steam-names.json.
/// </summary>
public class SteamNames(ServerFiles files, LogWatcher logs, ILogger<SteamNames> logger)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    readonly SemaphoreSlim gate = new(1, 1);
    Dictionary<string, (string Name, DateTimeOffset At)>? cache;
    string CachePath => Path.Combine(files.LogsDir, "Manager", "steam-names.json");

    public record NameInfo(string SteamId, string? InGame, string? Steam);

    public async Task<NameInfo> GetAsync(string steamId)
    {
        var inGame = logs.GetHistory().FirstOrDefault(h => h.SteamId == steamId)?.Name;
        return new NameInfo(steamId, inGame, await SteamPersonaAsync(steamId));
    }

    public async Task<Dictionary<string, NameInfo>> GetManyAsync(IEnumerable<string> ids)
    {
        var result = new Dictionary<string, NameInfo>();
        foreach (var id in ids.Distinct()) result[id] = await GetAsync(id);
        return result;
    }

    async Task<string?> SteamPersonaAsync(string steamId)
    {
        if (!Regex.IsMatch(steamId, @"^\d{17}$")) return null;
        await gate.WaitAsync();
        try
        {
            cache ??= Load();
            if (cache.TryGetValue(steamId, out var c) && DateTimeOffset.Now - c.At < TimeSpan.FromDays(7)) return c.Name;
            try
            {
                var xml = await Http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}/?xml=1");
                var m = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline);
                if (!m.Success) return c.Name;                      // keep a stale name rather than nothing
                cache[steamId] = (System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), DateTimeOffset.Now);
                Save();
                return cache[steamId].Name;
            }
            catch (Exception ex) { logger.LogDebug(ex, "Steam profile lookup failed for {Id}", steamId); return c.Name; }
        }
        finally { gate.Release(); }
    }

    Dictionary<string, (string, DateTimeOffset)> Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return new();
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CacheItem>>(File.ReadAllText(CachePath)) ?? new();
            return raw.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.At));
        }
        catch { return new(); }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(
                cache!.ToDictionary(kv => kv.Key, kv => new CacheItem(kv.Value.Name, kv.Value.At)), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { logger.LogDebug(ex, "Could not save Steam name cache"); }
    }

    record CacheItem(string Name, DateTimeOffset At);
}

// ============================================================ structures at risk of decay

public record DecayRisk(long EntityId, string Name, string Type, string Playfield, string? Owner, int Blocks, bool HasCore,
                        string Reason, double HoursLeft, bool Overdue);

/// <summary>
/// The game's DecayTime rule removes player structures that have NO core OR FEWER THAN 10 BLOCKS once they haven't been
/// visited (a player close enough to load them) for DecayTime hours. The check runs when the playfield next loads, so
/// "overdue" structures go the next time anyone enters that playfield. This lists them from a lock-free copy of global.db.
/// </summary>
public class DecayService(ServerFiles files, LogWatcher logs, GameOptionsService gameOptions, ILogger<DecayService> logger)
{
    const double TicksPerSecond = 20;                // server ticks advance ~20/s while it runs (see the log's INFO lines)
    const int MinBlocks = 10;
    readonly SemaphoreSlim gate = new(1, 1);
    (DateTime At, object Result)? cache;

    string CopyPath => Path.Combine(Path.GetTempPath(), "EmpyrionManager", "global-decay-copy.db");

    public int DecayHours()
    {
        var f = gameOptions.Get().Fields.FirstOrDefault(x => x.Def.Key == "DecayTime");
        return f?.Value is { } v && int.TryParse(v, out var h) ? h : 24;   // game default for multiplayer
    }

    long? NowTicks()
    {
        var i = logs.Info;
        if (i.Ticks is not long t || i.At is not DateTimeOffset at) return null;
        return t + (long)((DateTimeOffset.Now - at).TotalSeconds * TicksPerSecond);
    }

    public async Task<object> GetAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Result;
            var hours = DecayHours();
            var now = NowTicks();
            var items = new List<DecayRisk>();
            string? problem = null;
            if (hours <= 0) problem = "Decay is switched off (DecayTime 0).";
            else if (now == null) problem = "Waiting for the server to report its clock (once a minute while it runs).";
            else
            {
                var src = Path.Combine(files.GameDir, "global.db");
                if (!File.Exists(src)) problem = "The save's database doesn't exist yet.";
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!);
                    using (var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var to = new FileStream(CopyPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        from.CopyTo(to);
                    var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                        { DataSource = CopyPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
                    using var db = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                    db.Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        SELECT e.entityid, e.name, e.etype, p.name, o.name, s.cntblocks, s.coretype, s.lastvisitedticks
                        FROM Entities e
                        JOIN Structures s ON s.entityid = e.entityid
                        JOIN Playfields p ON p.pfid = e.pfid
                        LEFT JOIN Entities o ON o.entityid = e.belongstoentityid
                        WHERE e.isstructure = 1 AND e.isremoved = 0 AND s.playercreated = 1
                          AND (s.cntblocks < $min OR s.coretype < 1)
                        """;
                    cmd.Parameters.AddWithValue("$min", MinBlocks);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var blocks = r.IsDBNull(5) ? -1 : r.GetInt32(5);
                        var hasCore = !r.IsDBNull(6) && r.GetInt32(6) >= 1;
                        var visited = r.IsDBNull(7) ? 0 : r.GetInt64(7);
                        var left = hours - (now.Value - visited) / TicksPerSecond / 3600.0;
                        var reason = !hasCore && blocks is >= 0 and < MinBlocks ? $"no core, {blocks} blocks"
                                   : !hasCore ? "no core" : $"only {blocks} blocks (needs {MinBlocks}+)";
                        var type = (r.IsDBNull(2) ? 0 : r.GetInt32(2)) switch { 2 => "Base", 3 => "Capital vessel", 4 => "Small vessel", 5 => "Hover vessel", _ => "Structure" };
                        items.Add(new DecayRisk(r.GetInt64(0), r.IsDBNull(1) ? "?" : r.GetString(1), type, r.GetString(3),
                                                r.IsDBNull(4) ? null : r.GetString(4), blocks, hasCore, reason, Math.Round(left, 1), left <= 0));
                    }
                }
            }
            var result = new { decayHours = hours, minBlocks = MinBlocks, problem, items = items.OrderBy(i => i.HoursLeft).ToList() };
            cache = (DateTime.UtcNow, result);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Decay check failed");
            return new { decayHours = 0, minBlocks = MinBlocks, problem = "Couldn't read the save right now - will retry.", items = new List<DecayRisk>() };
        }
        finally { gate.Release(); }
    }
}
