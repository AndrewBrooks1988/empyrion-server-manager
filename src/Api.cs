using System.Diagnostics;

namespace EmpyrionManager;

public record WarnRequest(int? Warn);
public record TextRequest(string? Text);
public record ReasonRequest(string? Reason);
public record BanRequest(string? Duration);
public record RestoreRequest(string? Confirm);

public static class Api
{
    const string Dedi = "EmpyrionDedicated", Playfield = "EmpyrionPlayfieldServer";

    static string WarnArg(int? minutes) => minutes switch
    {
        null or <= 0 => "0",
        1 => "1",
        <= 5 => "5,1",
        <= 10 => "10,5,1",
        _ => "15,10,5,1",
    };

    static IResult Fail(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        // Simple CSRF guard: browsers can't add this header on cross-site form posts.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) &&
                ctx.Request.Headers["X-Manager"] != "1")
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-Manager header." });
                return;
            }
            await next();
        });

        var api = app.MapGroup("/api");

        // ---------------------------------------------------------- status
        api.MapGet("/status", (ServerFiles files, ProcessMonitor pm, LogWatcher logs, JobRunner jobs, BackupService backups, ManagerOptions o) =>
        {
            var dedi = pm.Get(Dedi);
            var pfs = pm.Get(Playfield);
            var running = dedi.Count > 0;
            var state = !running ? "offline" : logs.ConsoleReady ? "online" : "starting";
            var job = jobs.Current;
            return Results.Json(new
            {
                state,
                server = running ? dedi[0] : null,
                playfieldProcesses = pfs,
                totals = new
                {
                    cpuPercent = Math.Round(dedi.Sum(p => p.CpuPercent) + pfs.Sum(p => p.CpuPercent), 1),
                    memoryMB = dedi.Sum(p => p.MemoryMB) + pfs.Sum(p => p.MemoryMB),
                },
                info = running ? logs.Info : null,
                players = running ? logs.GetPlayers() : [],
                config = new
                {
                    name = files.Get("Srv_Name"),
                    port = files.Get("Srv_Port", "30000"),
                    maxPlayers = files.Get("Srv_MaxPlayers", "8"),
                    game = files.GameName,
                    scenario = files.Get("CustomScenario"),
                    mode = files.Get("Mode"),
                    hasPassword = files.Get("Srv_Password").Length > 0,
                },
                gameClientRunning = ProcessMonitor.IsRunning("Empyrion"),
                job = job == null ? null : new { job.Id, job.Kind, job.Title, job.State, job.Started, job.Ended },
                logFile = logs.CurrentFile is { } f ? Path.GetFileName(f) : null,
                backupCount = backups.List().Count,
                now = DateTimeOffset.Now,
                setup = new { configured = o.Configured, title = o.Title, serverDirValid = o.ServerDirValid, configValid = o.ConfigValid },
            });
        });

        api.MapGet("/activity", (LogWatcher logs) => Results.Json(logs.GetActivity(60)));

        api.MapGet("/player-history", (LogWatcher logs) => Results.Json(logs.GetHistory()));

        api.MapGet("/chat", async (ChatService chat) => Results.Json(await chat.GetEntriesAsync()));

        api.MapGet("/log", (LogWatcher logs, long? after, int? max) =>
            Results.Json(logs.GetLines(after ?? 0, Math.Clamp(max ?? 400, 1, 2000))));

        // ---------------------------------------------------------- jobs
        api.MapGet("/job", (JobRunner jobs, int? from) =>
        {
            var j = jobs.Current;
            if (j == null) return Results.Json(new { job = (object?)null });
            return Results.Json(new
            {
                job = new { j.Id, j.Kind, j.Title, j.State, j.Started, j.Ended, count = j.Count },
                lines = j.Lines(from ?? 0),
            });
        });

        // ---------------------------------------------------------- server control
        api.MapPost("/server/start", (JobRunner jobs, ManagerOptions o, LogWatcher logs) =>
        {
            if (ProcessMonitor.IsRunning(Dedi)) return Fail("The server is already running.");
            try
            {
                var job = jobs.Start("start", "Start server", async job =>
                {
                    var previousLog = logs.CurrentFile;
                    job.Add($"Launching server: EmpyrionLauncher.exe {o.LaunchMode} -dedicated {o.ConfigFile}");
                    Process.Start(new ProcessStartInfo(o.LauncherPath, $"{o.LaunchMode} -dedicated {o.ConfigFile}")
                        { WorkingDirectory = o.ServerDir, UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                    var deadline = DateTime.Now.AddMinutes(15);
                    var seenProcess = false;
                    while (DateTime.Now < deadline)
                    {
                        await Task.Delay(3000);
                        var running = ProcessMonitor.IsRunning(Dedi);
                        if (running && !seenProcess) { seenProcess = true; job.Add("Server process started - loading the galaxy..."); }
                        if (seenProcess && !running) throw new InvalidOperationException("The server process exited during startup - check the log.");
                        if (running && logs.CurrentFile != previousLog && logs.ConsoleReady)
                        {
                            job.Add("Server is up and accepting console commands.");
                            return;
                        }
                    }
                    throw new InvalidOperationException("The server didn't report ready within 15 minutes - check the log.");
                });
                return Results.Json(new { job.Id });
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        api.MapPost("/server/{action}", (string action, WarnRequest? req, JobRunner jobs) =>
        {
            var warn = WarnArg(req?.Warn);
            (string mode, string title)? spec = action switch
            {
                "stop" => ("Stop", "Stop server"),
                "restart" => ("Restart", "Restart server"),
                "play" => ("Play", "Restart around game launch (Play)"),
                _ => null,
            };
            if (spec == null) return Results.NotFound();
            if (!ProcessMonitor.IsRunning(Dedi) && action != "play") return Fail("The server isn't running.");
            var args = action == "play" ? "-Mode Play" : $"-Mode {spec.Value.mode} -Warn {warn}";
            try { return Results.Json(new { jobs.RunScript(action, spec.Value.title, args).Id }); }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        api.MapPost("/maintenance/{kind}", (string kind, WarnRequest? req, JobRunner jobs) =>
        {
            var mode = kind switch { "daily" => "Daily", "weekly" => "Weekly", _ => null };
            if (mode == null) return Results.NotFound();
            if (!ProcessMonitor.IsRunning(Dedi)) return Fail("The server isn't running - maintenance only runs on a running server.");
            var title = kind == "daily" ? "Daily maintenance (run now)" : "Weekly reset (run now)";
            try { return Results.Json(new { jobs.RunScript(kind, title, $"-Mode {mode} -Warn {WarnArg(req?.Warn)}").Id }); }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        api.MapGet("/maintenance/log", (ServerFiles files) =>
        {
            var dir = new DirectoryInfo(files.MaintenanceLogDir);
            if (!dir.Exists) return Results.Json(Array.Empty<string>());
            var latest = dir.EnumerateFiles("*.log").OrderByDescending(f => f.Name).FirstOrDefault();
            if (latest == null) return Results.Json(Array.Empty<string>());
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
            return Results.Json(all.TakeLast(300).Reverse());
        });

        // ---------------------------------------------------------- chat / console / players
        api.MapPost("/say", async (TextRequest req, TelnetService tel, ChatService chat) =>
        {
            var text = req.Text?.Trim() ?? "";
            if (text.Length == 0) return Fail("Type a message first.");
            if (text.Length > 200) return Fail("Keep messages under 200 characters.");
            try
            {
                var output = await tel.SendOneAsync("say " + TelnetService.Quote(text));
                chat.RecordSent("All", text);
                return Results.Json(new { output, text });
            }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        api.MapPost("/console", async (TextRequest req, TelnetService tel) =>
        {
            var cmd = req.Text?.Trim() ?? "";
            if (cmd.Length == 0) return Fail("Type a command first.");
            try { return Results.Json(new { output = await tel.SendOneAsync(cmd, 800, 10000) }); }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        api.MapPost("/players/{steamId}/kick", async (string steamId, ReasonRequest req, TelnetService tel) =>
        {
            if (!IsSteamId(steamId)) return Fail("That isn't a SteamID64.");
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Kicked by admin" : req.Reason!;
            try { return Results.Json(new { output = await tel.SendOneAsync($"kick {steamId} {TelnetService.Quote(reason)}") }); }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        // ban / mute durations: hours, days or months, e.g. 2h, 14d, 1m (the server has no years)
        foreach (var verb in new[] { "ban", "mute" })
        {
            api.MapPost($"/players/{{steamId}}/{verb}", async (string steamId, BanRequest req, TelnetService tel) =>
            {
                if (!IsSteamId(steamId)) return Fail("That isn't a SteamID64.");
                var duration = req.Duration?.Trim() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(duration, @"^\d{1,3}[hdm]$")) return Fail("Duration must look like 2h, 14d or 1m (months).");
                try { return Results.Json(new { output = await tel.SendOneAsync($"{verb} {steamId} {duration}") }); }
                catch (Exception ex) { return Fail(ex.Message, 502); }
            });
        }

        api.MapPost("/players/{steamId}/message", async (string steamId, TextRequest req, TelnetService tel, LogWatcher logs, ChatService chat) =>
        {
            var text = req.Text?.Trim() ?? "";
            if (text.Length == 0) return Fail("Type a message first.");
            var p = logs.GetPlayers().FirstOrDefault(x => x.SteamId == steamId);
            if (p?.EntityId is not int eid) return Fail("That player isn't fully loaded in yet - try again in a moment.");
            try
            {
                var output = await tel.SendOneAsync($"say p:{eid} {TelnetService.Quote(text)}");
                chat.RecordSent(p.Name, text);
                return Results.Json(new { output });
            }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        api.MapGet("/bans", async (TelnetService tel) =>
        {
            try
            {
                var output = await tel.SendOneAsync("list banned");
                var bans = System.Text.RegularExpressions.Regex.Matches(output, @"(\d{17})\s*-\s*(.+)")
                    .Select(m => new { steamId = m.Groups[1].Value, until = m.Groups[2].Value.Trim() }).ToList();
                return Results.Json(new { bans });
            }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        api.MapPost("/bans/{steamId}/unban", async (string steamId, TelnetService tel) =>
        {
            if (!IsSteamId(steamId)) return Fail("That isn't a SteamID64.");
            try { return Results.Json(new { output = await tel.SendOneAsync($"unban {steamId}") }); }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        api.MapGet("/known-players", async (TelnetService tel, LogWatcher logs, AdminService admins) =>
        {
            try
            {
                var output = await tel.SendOneAsync("plys");
                var idx = output.IndexOf("Global players list", StringComparison.OrdinalIgnoreCase);
                var section = idx >= 0 ? output[idx..] : output;
                var players = System.Text.RegularExpressions.Regex.Matches(section,
                        @"id=(\d+)\s+name=(.*?)\s+fac=\[(.*?)\]\s+role=(\S+)")
                    .Select(m => new { entityId = int.Parse(m.Groups[1].Value), name = m.Groups[2].Value, faction = m.Groups[3].Value, role = m.Groups[4].Value })
                    .ToList();
                // link each character to a SteamID (from the player history) and their server permission (adminconfig)
                var history = logs.GetHistory();
                var perms = admins.Read().ToDictionary(x => x.SteamId, x => x.Permission);
                var enriched = players.Select(p =>
                {
                    var sid = history.FirstOrDefault(h => h.EntityId == p.entityId)?.SteamId
                              ?? history.FirstOrDefault(h => h.Name == p.name)?.SteamId;
                    var perm = sid != null && perms.TryGetValue(sid, out var pm) ? pm : 0;
                    return new { p.entityId, p.name, p.faction, p.role, steamId = sid, permission = perm };
                }).ToList();
                return Results.Json(new { players = enriched });
            }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        // ---------------------------------------------------------- backups
        api.MapGet("/backups", (BackupService backups) => Results.Json(backups.List()));

        api.MapPost("/backups", (BackupService backups, ServerFiles files, JobRunner jobs) =>
        {
            if (ProcessMonitor.IsRunning(Dedi)) return Fail("Stop the server first - backups of a running save can be inconsistent. (Scheduled maintenance backs up daily.)");
            try
            {
                var name = backups.NewName("manual");
                var job = jobs.Start("backup", "Manual backup", async job =>
                    await BackupService.CopyAsync(files.GameDir, Path.Combine(files.BackupDir, name), false, job));
                return Results.Json(new { job.Id });
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        api.MapPost("/backups/{name}/restore", (string name, RestoreRequest req, BackupService backups, ServerFiles files, JobRunner jobs) =>
        {
            if (ProcessMonitor.IsRunning(Dedi)) return Fail("Stop the server before restoring a backup.");
            var source = Path.Combine(files.BackupDir, name);
            if (name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(source))
                return Fail("Backup not found.", 404);
            if (req.Confirm != name) return Fail("Type the backup's exact name to confirm the restore.");
            try
            {
                var safety = backups.NewName("pre-restore");
                var job = jobs.Start("restore", $"Restore {name}", async job =>
                {
                    job.Add("Saving the current world first, in case you want it back...");
                    await BackupService.CopyAsync(files.GameDir, Path.Combine(files.BackupDir, safety), false, job);
                    job.Add($"Restoring {name}...");
                    await BackupService.CopyAsync(source, files.GameDir, true, job);
                    job.Add($"Restored. The previous world is kept as {safety}.");
                });
                return Results.Json(new { job.Id });
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message, 409); }
        });

        // ---------------------------------------------------------- remote access (Tailscale Serve)
        api.MapGet("/remote", async (TailscaleService ts) => Results.Json(await ts.GetAsync()));

        api.MapPost("/remote/{op}", async (string op, TailscaleService ts) =>
        {
            if (op is not ("on" or "off")) return Results.NotFound();
            try
            {
                var output = await ts.SetServedAsync(op == "on");
                return Results.Json(new { output, state = await ts.GetAsync(fresh: true) });
            }
            catch (Exception ex) { return Fail(ex.Message, 502); }
        });

        // ---------------------------------------------------------- settings
        api.MapGet("/settings", (ServerFiles files, ManagerOptions o) => Results.Json(new
        {
            settings = files.ReadForDisplay(),
            paths = new
            {
                serverDir = o.ServerDir, config = o.ConfigPath, save = files.GameDir,
                backups = files.BackupDir, logs = files.LogsDir, script = o.ScriptPath,
            },
            manager = new { url = o.Url, launchMode = o.LaunchMode, version = typeof(Api).Assembly.GetName().Version?.ToString(3) },
        }));
    }

    static bool IsSteamId(string s) => s.Length == 17 && s.All(char.IsDigit);
}
