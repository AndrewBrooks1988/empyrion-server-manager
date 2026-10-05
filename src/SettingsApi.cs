using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmpyrionManager;

public record SetupRequest(string? ServerDir, string? ConfigFile, string? LaunchMode, string? Title, string? Url, string? SteamCmdPath);
public record NewConfigRequest(string? Name, string? CopyFrom);
public record ValuesRequest(Dictionary<string, string?>? Values);
public record AdminsRequest(List<AdminEntry>? Admins, List<string>? Priority);
public record PasswordRequest(string? Password);
public record TaskNamesRequest(string? Folder, string? DailyName, string? WeeklyName);
public record RoleRequest(string? Role, string? Name, int? EntityId);
public record PrefsRequest(bool? CheckForUpdates, bool? OpenBrowserOnStart);

/// <summary>Endpoints behind the Settings pages: setup, server config, game rules, admins, schedule and tasks.</summary>
public static class SettingsApi
{
    const string Dedi = "EmpyrionDedicated";
    static IResult Fail(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);
    static bool Running => ProcessMonitor.IsRunning(Dedi);
    /// <summary>Config edits always apply when the server next starts; say which that is.</summary>
    static string WhenApplied => Running ? "Saved. Takes effect on the next restart." : "Saved. Takes effect when the server starts.";

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------------------------------------------------------- setup (server folder, config file, manager)
        api.MapGet("/setup", (ManagerOptions o, ServerFiles files, ScenarioService scen) =>
        {
            var steamCmd = o.SteamCmdPath.Length > 0 && File.Exists(o.SteamCmdPath) ? o.SteamCmdPath : Setup.DetectSteamCmd(o.ServerDir);
            return Results.Json(new
            {
                configured = o.Configured,
                settingsFileExists = File.Exists(o.SettingsPath),
                serverDir = o.ServerDir,
                serverDirValid = o.ServerDirValid,
                configFile = o.ConfigFile,
                configValid = o.ConfigValid,
                configFiles = Setup.ConfigFiles(o.ServerDir),
                launchMode = o.LaunchMode,
                title = o.Title,
                url = o.Url,
                steamCmdPath = o.SteamCmdPath,
                steamCmdDetected = steamCmd,
                hasPassword = o.AccessPasswordHash.Length > 0,
                telnet = o.ConfigValid ? new
                {
                    enabled = files.Get("Tel_Enabled").Equals("true", StringComparison.OrdinalIgnoreCase),
                    port = files.Get("Tel_Port"),
                    hasPassword = files.Get("Tel_Pwd").Length > 0,
                } : null,
                scenarios = o.ServerDirValid ? scen.Scenarios() : [],
                appDir = o.AppDir,
                settingsPath = o.SettingsPath,
                running = Running,
            });
        });

        api.MapGet("/setup/detect", (ManagerOptions o) => Results.Json(new { dirs = Setup.DetectServerDirs(o.AppDir) }));

        api.MapPost("/setup", (SetupRequest req, ManagerOptions o) =>
        {
            var dir = req.ServerDir?.Trim().Trim('"') ?? o.ServerDir;
            if (dir.Length == 0 || !File.Exists(Path.Combine(dir, "EmpyrionLauncher.exe")))
                return Fail("That folder isn't an Empyrion dedicated server (no EmpyrionLauncher.exe in it).");
            var config = req.ConfigFile?.Trim() ?? o.ConfigFile;
            if (config.Length == 0 || config.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !File.Exists(Path.Combine(dir, config)))
                return Fail("Pick a config file that exists in the server folder (or create a new one).");
            var launch = req.LaunchMode is "-startDedi" or "-startDediWithGfx" ? req.LaunchMode : o.LaunchMode;
            var url = req.Url?.Trim() ?? o.Url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "http") return Fail("Dashboard address must look like http://127.0.0.1:8090");
            var restart = !string.Equals(url, o.Url, StringComparison.OrdinalIgnoreCase);
            var steamCmd = req.SteamCmdPath?.Trim().Trim('"') ?? o.SteamCmdPath;
            if (steamCmd.Length > 0 && !File.Exists(steamCmd)) return Fail("SteamCMD path doesn't point at steamcmd.exe.");

            o.ServerDir = Path.GetFullPath(dir);
            o.ConfigFile = config;
            o.LaunchMode = launch!;
            o.Title = string.IsNullOrWhiteSpace(req.Title) ? o.Title : req.Title.Trim();
            o.Url = url;
            o.SteamCmdPath = steamCmd;
            o.Save();
            return Results.Json(new
            {
                ok = true,
                message = restart ? "Saved. Restart the manager to use the new dashboard address." : "Saved.",
                managerRestartRequired = restart,
            });
        });

        api.MapPost("/setup/config-file", (NewConfigRequest req, ManagerOptions o) =>
        {
            if (!o.ServerDirValid) return Fail("Set the server folder first.");
            var name = req.Name?.Trim() ?? "";
            if (!name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) name += ".yaml";
            if (!Regex.IsMatch(name, @"^[\w\- .]+\.yaml$")) return Fail("Use a simple file name, e.g. my-server.yaml");
            var path = Path.Combine(o.ServerDir, name);
            if (File.Exists(path)) return Fail($"{name} already exists.");
            if (!string.IsNullOrWhiteSpace(req.CopyFrom))
            {
                var src = Path.Combine(o.ServerDir, Path.GetFileName(req.CopyFrom));
                if (!File.Exists(src)) return Fail("The file to copy doesn't exist.");
                File.Copy(src, path);
            }
            else File.WriteAllText(path, ServerConfigService.NewConfigTemplate().Replace("\n", "\r\n"));
            return Results.Json(new { ok = true, file = name });
        });

        api.MapPost("/setup/telnet", (ManagerOptions o, ServerConfigService config, ServerFiles files) =>
        {
            if (!o.ConfigValid) return Fail("Pick the server config file first.");
            config.EnableTelnet(files);
            return Results.Json(new { ok = true, message = "Telnet enabled with a generated password. " + WhenApplied });
        });

        api.MapPost("/setup/password", (PasswordRequest req, ManagerOptions o) =>
        {
            if (req.Password is { Length: > 0 and < 8 }) return Fail("Use at least 8 characters.");
            o.SetPassword(req.Password);
            o.Save();
            return Results.Json(new { ok = true, message = req.Password is { Length: > 0 } ? "Password set. Other devices will be asked for it." : "Password removed." });
        });

        api.MapPost("/server/update", (ManagerOptions o, JobRunner jobs) =>
        {
            if (Running) return Fail("Stop the server before updating it.");
            var steamCmd = o.SteamCmdPath.Length > 0 && File.Exists(o.SteamCmdPath) ? o.SteamCmdPath : Setup.DetectSteamCmd(o.ServerDir);
            if (steamCmd == null) return Fail("SteamCMD not found. Set its path in Settings → Setup.");
            try { return Results.Json(new { jobs.Start("update", "Update server (SteamCMD)", job => Setup.UpdateServerAsync(steamCmd, o.ServerDir, job)).Id }); }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        // ---------------------------------------------------------- updates + about
        api.MapGet("/update", (UpdateService up, ManagerOptions o) => Results.Json(new { state = up.State, o.CheckForUpdates, o.OpenBrowserOnStart }));
        api.MapPost("/update/check", async (UpdateService up) => Results.Json(new { state = await up.CheckAsync() }));
        api.MapPost("/update/install", async (UpdateService up) =>
        {
            try { await up.InstallAsync(); return Results.Json(new { ok = true, message = "Installing the update. The manager will restart in a few seconds." }); }
            catch (Exception ex) { return Fail(ex.Message); }
        });
        api.MapPost("/update/prefs", (PrefsRequest req, ManagerOptions o) =>
        {
            if (req.CheckForUpdates is bool c) o.CheckForUpdates = c;
            if (req.OpenBrowserOnStart is bool b) o.OpenBrowserOnStart = b;
            o.Save();
            return Results.Json(new { ok = true });
        });

        // ---------------------------------------------------------- server config (.yaml)
        api.MapGet("/server-config", (ServerConfigService config, ScenarioService scen, ManagerOptions o) =>
        {
            if (!o.ConfigValid) return Fail("Finish setup first.");
            return Results.Json(new
            {
                file = o.ConfigFile,
                running = Running,
                scenarios = scen.Scenarios(),
                fields = config.Get().Select(f => new
                {
                    f.Def.Key, f.Def.Section, f.Def.Label, f.Def.Type, f.Def.Help, f.Def.Options, f.Def.Secret, f.Def.Required, f.Def.Group,
                    f.Value, f.IsSet,
                }),
            });
        });

        api.MapPost("/server-config", (ValuesRequest req, ServerConfigService config) =>
        {
            if (req.Values == null || req.Values.Count == 0) return Fail("Nothing to save.");
            try
            {
                var changed = config.Save(req.Values);
                return Results.Json(new { changed, running = Running, message = changed.Count == 0 ? "No changes." : WhenApplied });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Fail(ex.Message); }
        });

        // ---------------------------------------------------------- game rules (gameoptions.yaml)
        api.MapGet("/game-options", (GameOptionsService go, ManagerOptions o) =>
        {
            if (!o.ConfigValid) return Fail("Finish setup first.");
            var (path, isSave, mode, fields, problem) = go.Get();
            return Results.Json(new
            {
                file = path, isSave, mode, problem, running = Running,
                fields = fields.Select(f => new { f.Def.Key, f.Def.Label, f.Def.Type, f.Def.Help, f.Def.Options, f.Def.Group, f.Value, f.IsSet }),
            });
        });

        api.MapPost("/game-options", (ValuesRequest req, GameOptionsService go) =>
        {
            if (req.Values == null || req.Values.Count == 0) return Fail("Nothing to save.");
            try
            {
                var changed = go.Save(req.Values);
                return Results.Json(new { changed, message = changed.Count == 0 ? "No changes." : WhenApplied + " Some difficulty settings only apply to a new save." });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Fail(ex.Message); }
        });

        // ---------------------------------------------------------- admins + login priority
        api.MapGet("/admins", async (AdminService admins, SteamNames names, ManagerOptions o) =>
        {
            if (!o.ConfigValid) return Fail("Finish setup first.");
            var list = admins.Read();
            var priority = admins.ReadPriority();
            var resolved = await names.GetManyAsync(list.Select(a => a.SteamId).Concat(priority));
            return Results.Json(new { admins = list, priority, names = resolved, file = admins.Path, running = Running });
        });

        api.MapGet("/steam-name/{steamId}", async (string steamId, SteamNames names) =>
            Regex.IsMatch(steamId, @"^\d{17}$") ? Results.Json(await names.GetAsync(steamId)) : Fail("Not a SteamID64."));

        // set one player's server role (from the Known players list)
        api.MapPost("/players/{steamId}/role", async (string steamId, RoleRequest req, AdminService admins, TelnetService tel) =>
        {
            if (!Regex.IsMatch(steamId, @"^\d{17}$")) return Fail("Not a SteamID64.");
            var role = (req.Role ?? "").ToLowerInvariant();
            var perm = role switch { "admin" => 9, "moderator" => 6, "gamemaster" => 3, "player" => 0, _ => -1 };
            if (perm < 0) return Fail("Role must be player, gamemaster, moderator or admin.");
            try { admins.SetRole(steamId, perm, req.Name); } catch (ArgumentException ex) { return Fail(ex.Message); }
            string? output = null;
            if (Running)
            {
                try
                {
                    output = (await tel.SendOneAsync($"setrole {steamId} {role}")).Split('\n')[0].Trim();
                    // setrole also accepts the in-game id; retry with it if the SteamID wasn't recognised
                    if (req.EntityId is int eid && Regex.IsMatch(output, "not found|unknown|no player|invalid", RegexOptions.IgnoreCase))
                        output = (await tel.SendOneAsync($"setrole {eid} {role}")).Split('\n')[0].Trim();
                }
                catch (Exception ex) { output = "Not applied live (" + ex.Message + "). It applies on the next restart."; }
            }
            return Results.Json(new
            {
                ok = true, output,
                message = Running ? $"{req.Name ?? steamId} is now {role}. Applied to the running server and saved." : $"{req.Name ?? steamId} will be {role} when the server starts.",
            });
        });

        api.MapPost("/admins", async (AdminsRequest req, AdminService admins, TelnetService tel) =>
        {
            var list = req.Admins ?? [];
            var before = admins.Read();
            try { admins.Save(list, req.Priority ?? []); }
            catch (ArgumentException ex) { return Fail(ex.Message); }

            // While the server runs, apply role changes immediately with setrole (the file covers future restarts).
            var applied = new List<string>();
            if (Running)
            {
                var commands = new List<string>();
                foreach (var a in list)
                    if (before.FirstOrDefault(b => b.SteamId == a.SteamId)?.Permission != a.Permission)
                        commands.Add($"setrole {a.SteamId} {AdminService.RoleName(a.Permission)}");
                foreach (var b in before.Where(b => list.All(a => a.SteamId != b.SteamId)))
                    commands.Add($"setrole {b.SteamId} player");
                if (commands.Count > 0)
                {
                    try { applied = (await tel.SendAsync(commands)).Select(r => $"{r.Command}: {r.Output.Split('\n')[0]}").ToList(); }
                    catch (Exception ex) { applied.Add("Couldn't apply live (" + ex.Message + ") - changes apply on the next restart."); }
                }
            }
            return Results.Json(new
            {
                ok = true, applied,
                message = Running
                    ? (applied.Count > 0 ? "Saved and applied to the running server. Login priority takes effect on the next restart." : "Saved. Login priority takes effect on the next restart.")
                    : "Saved. Takes effect when the server starts.",
            });
        });

        // ---------------------------------------------------------- maintenance schedule settings
        api.MapGet("/maintenance/settings", (ManagerOptions o, ScenarioService scen, TaskService tasks) => Results.Json(new
        {
            maintenance = o.Maintenance,
            tasks = o.Tasks,
            taskStartTime = tasks.StartTime(),
            taskStartTime2 = o.Maintenance.TwiceDaily ? tasks.StartTime(12) : null,
            solarSystems = o.ConfigValid ? scen.SolarSystems() : [],
        }));

        api.MapPost("/maintenance/settings", async (JsonElement body, ManagerOptions o) =>
        {
            MaintenanceSettings? m;
            try { m = body.GetProperty("maintenance").Deserialize<MaintenanceSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return Fail("Invalid maintenance settings."); }
            if (m == null) return Fail("Invalid maintenance settings.");
            if (!TimeSpan.TryParse(m.RestartTime, out var rt) || rt < TimeSpan.Zero || rt >= TimeSpan.FromDays(1)) return Fail("Restart time must look like 04:00.");
            if (m.WarnMinutes.Any(w => w < 1 || w > 120)) return Fail("Warnings must be between 1 and 120 minutes.");
            if (m.BackupsToKeep is < 1 or > 365) return Fail("Keep between 1 and 365 backups.");
            var validWipe = new Regex(@"^(\s*(poi|deposit|terrain|player)\s*)*$");
            if (!validWipe.IsMatch(m.DailyStarterWipe) || !validWipe.IsMatch(m.DailySpaceWipe) || !validWipe.IsMatch(m.WeeklyWipe))
                return Fail("Wipe types can only be poi, deposit, terrain or player.");
            string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
            if (m.DailyDays.Concat(m.WeeklyDays).Any(d => !days.Contains(d))) return Fail("Unknown day.");
            if (m.DailyDays.Intersect(m.WeeklyDays).Any()) return Fail("A day can't have both the daily and the weekly run.");
            m.WarnMinutes = m.WarnMinutes.Distinct().OrderByDescending(x => x).ToArray();

            if (body.TryGetProperty("tasks", out var t))
            {
                var names = t.Deserialize<TaskSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (names != null)
                {
                    if (string.IsNullOrWhiteSpace(names.DailyName) || string.IsNullOrWhiteSpace(names.WeeklyName) || names.DailyName == names.WeeklyName)
                        return Fail("Give the two tasks different names.");
                    if (!names.Folder.StartsWith('\\')) names.Folder = "\\" + names.Folder;
                    o.Tasks = names;
                }
            }
            o.Maintenance = m;
            o.Save();
            await Task.CompletedTask;
            return Results.Json(new { ok = true, message = "Saved. Use \"Install / update\" on each task so Task Scheduler picks up any schedule changes." });
        });

        // ---------------------------------------------------------- scheduled tasks
        api.MapGet("/tasks", async (TaskService tasks) => Results.Json(await tasks.GetAllAsync()));

        api.MapPost("/tasks/{key}/{op}", async (string key, string op, TaskService tasks) =>
        {
            if (!tasks.Keys.Contains(key)) return Results.NotFound();
            try
            {
                switch (op)
                {
                    case "install": await tasks.InstallAsync(key); break;
                    case "remove": await tasks.RemoveAsync(key); break;
                    case "enable": await tasks.SetEnabledAsync(key, true); break;
                    case "disable": await tasks.SetEnabledAsync(key, false); break;
                    default: return Results.NotFound();
                }
                return Results.Json(new { ok = true });
            }
            catch (Exception ex) { return Fail(ex.Message); }
        });
    }
}
