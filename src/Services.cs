using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace EmpyrionManager;

// ============================================================ settings.yaml

public record Setting(string Section, string Key, string Value);

public class ServerFiles(ManagerOptions o)
{
    static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase) { "Tel_Pwd", "Srv_Password" };

    /// <summary>Flat read of the active (uncommented) keys in settings.yaml, in file order.</summary>
    public List<Setting> ReadAll()
    {
        var list = new List<Setting>();
        if (!File.Exists(o.ConfigPath)) return list;
        var section = "";
        foreach (var raw in File.ReadAllLines(o.ConfigPath))
        {
            if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0) continue;
            var m = Regex.Match(raw, @"^(\s*)([A-Za-z_]\w*)\s*:\s*(.*)$");
            if (!m.Success) continue;
            var value = Unquote(StripComment(m.Groups[3].Value).Trim());
            if (m.Groups[1].Value.Length == 0 && value.Length == 0) { section = m.Groups[2].Value; continue; }
            list.Add(new Setting(section, m.Groups[2].Value, value));
        }
        return list;
    }

    public string Get(string key, string fallback = "")
    {
        var s = ReadAll().LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return s is { Value.Length: > 0 } ? s.Value : fallback;
    }

    public List<Setting> ReadForDisplay() =>
        ReadAll().Select(s => Secret.Contains(s.Key) ? s with { Value = "•••••• (hidden)" } : s).ToList();

    public string GameName => Get("GameName");
    public string SaveDir => Path.Combine(o.ServerDir, Get("SaveDirectory", "Saves"));
    public string GameDir => Path.Combine(SaveDir, "Games", GameName);
    public string BackupDir => Path.Combine(o.ServerDir, "Backups");
    public string LogsDir => Path.Combine(o.ServerDir, "Logs");
    public string MaintenanceLogDir => Path.Combine(LogsDir, "Maintenance");
    public int TelnetPort => int.TryParse(Get("Tel_Port", "30004"), out var p) ? p : 30004;

    static string StripComment(string v)
    {
        bool inQuote = false; char q = '\0';
        for (int i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (inQuote) { if (c == q) inQuote = false; }
            else if (c is '"' or '\'') { inQuote = true; q = c; }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(v[i - 1]))) return v[..i];
        }
        return v;
    }

    static string Unquote(string v) =>
        v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
}

// ============================================================ telnet

public record TelnetResult(string Command, string Output);

public class TelnetService(ServerFiles files) : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    TcpClient? client;
    NetworkStream? stream;

    /// <summary>
    /// Runs commands over one long-lived, logged-in Telnet session (reconnecting when needed).
    /// Keeping the session open avoids the server logging thread start/stop and
    /// "connection closed while writing" lines for every request.
    /// </summary>
    public async Task<List<TelnetResult>> SendAsync(IEnumerable<string> commands, int idleMs = 500, int maxMs = 6000)
    {
        await gate.WaitAsync();
        try
        {
            var results = new List<TelnetResult>();
            foreach (var cmd in commands)
            {
                string output;
                try { output = await RunAsync(cmd, idleMs, maxMs); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    Drop();                                      // stale session - reconnect once and retry
                    output = await RunAsync(cmd, idleMs, maxMs);
                }
                results.Add(new TelnetResult(cmd, output.Trim()));
            }
            return results;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Drop();
            throw new InvalidOperationException("Lost the connection to the server console (Telnet). Is the server running?");
        }
        finally { gate.Release(); }
    }

    async Task<string> RunAsync(string cmd, int idleMs, int maxMs)
    {
        var s = await EnsureConnectedAsync();
        await ReadQuiet(s, 0, 0);                                // discard anything unsolicited
        await WriteLine(s, cmd);
        var output = await ReadQuiet(s, idleMs, maxMs);
        if (output.Length == 0 && IsClosed()) throw new IOException("Telnet session closed.");
        return output;
    }

    async Task<NetworkStream> EnsureConnectedAsync()
    {
        if (client is { Connected: true } && stream != null && !IsClosed()) return stream;
        Drop();
        var c = new TcpClient();
        using (var cts = new CancellationTokenSource(3000))
        {
            try { await c.ConnectAsync("127.0.0.1", files.TelnetPort, cts.Token); }
            catch (Exception) { c.Dispose(); throw new InvalidOperationException("Can't reach the server console (Telnet). Is the server running?"); }
        }
        var s = c.GetStream();
        await ReadQuiet(s, 700, 3000);                           // "Enter password:"
        var pwd = files.Get("Tel_Pwd");
        if (pwd.Length > 0)
        {
            await WriteLine(s, pwd);
            var login = await ReadQuiet(s, 700, 4000);
            if (login.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                !login.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                c.Dispose();
                throw new InvalidOperationException("Telnet login failed - check the Telnet password in the server config.");
            }
        }
        client = c; stream = s;
        return s;
    }

    bool IsClosed()
    {
        try { return client == null || (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0); }
        catch { return true; }
    }

    void Drop()
    {
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
        stream = null; client = null;
    }

    public void Dispose() => Drop();

    public async Task<string> SendOneAsync(string command, int idleMs = 600, int maxMs = 8000) =>
        (await SendAsync([command], idleMs, maxMs))[0].Output;

    public static string Quote(string text) => "'" + text.Replace("'", "").Replace("\r", " ").Replace("\n", " ").Trim() + "'";

    static Task WriteLine(NetworkStream s, string line) => s.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n")).AsTask();

    static async Task<string> ReadQuiet(NetworkStream s, int idleMs, int maxMs)
    {
        var sb = new StringBuilder();
        var buf = new byte[8192];
        long start = Environment.TickCount64, last = start;
        while (true)
        {
            if (s.DataAvailable)
            {
                int n = await s.ReadAsync(buf);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                last = Environment.TickCount64;
            }
            else
            {
                if (Environment.TickCount64 - last >= idleMs || Environment.TickCount64 - start >= maxMs) break;
                await Task.Delay(40);
            }
        }
        return sb.ToString();
    }
}
// ============================================================ processes

public record ProcInfo(int Pid, string Name, DateTime Started, double CpuPercent, long MemoryMB);

public class ProcessMonitor
{
    readonly Dictionary<int, (TimeSpan Cpu, long Tick)> last = new();
    readonly object sync = new();

    /// <summary>The managed server's folder. Only processes started from it count, so a second server on the PC is ignored.</summary>
    public static Func<string>? ServerDir { get; set; }

    static bool Ours(Process p)
    {
        var dir = ServerDir?.Invoke();
        if (string.IsNullOrEmpty(dir)) return false;                 // not set up yet: don't claim anyone's server
        try { return p.MainModule?.FileName?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ?? true; }
        catch { return true; }                                       // path unreadable (e.g. elevated): assume it's ours
    }

    public List<ProcInfo> Get(string processName)
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!Ours(p)) continue;
                var cpu = p.TotalProcessorTime;
                var tick = Environment.TickCount64;
                double pct = 0;
                lock (sync)
                {
                    if (last.TryGetValue(p.Id, out var prev) && tick > prev.Tick)
                        pct = (cpu - prev.Cpu).TotalMilliseconds / (tick - prev.Tick) / Environment.ProcessorCount * 100;
                    last[p.Id] = (cpu, tick);
                }
                list.Add(new ProcInfo(p.Id, processName, p.StartTime, Math.Round(Math.Max(0, pct), 1), p.WorkingSet64 / 1048576));
            }
            catch { /* process exited between enumerate and read */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static bool IsRunning(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        try { return ps.Any(p => processName == "Empyrion" || Ours(p)); }   // the game client never lives in the server folder
        finally { foreach (var p in ps) p.Dispose(); }
    }
}

// ============================================================ dedicated log watcher

public record LogLine(long Seq, DateTimeOffset? Time, string Text, string Kind);

/// <summary>Everyone who has logged in, persisted across restarts (Logs\Manager\players.json).</summary>
public class PlayerHistory
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset LastLogin { get; set; }
    public int Visits { get; set; }
    public int? EntityId { get; set; }                 // set once the player has spawned (has a character)
    public string? LastVisit { get; set; }             // how the last visit ended, e.g. "Left while loading"
}
public record Activity(DateTimeOffset Time, string Kind, string Text);

public class PlayerState
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public string? Playfield { get; set; }
    public int? EntityId { get; set; }
}

public class ServerInfo
{
    public string? Uptime { get; set; }
    public double? Fps { get; set; }
    public int? HeapMB { get; set; }
    public int? Players { get; set; }
    public string? Playfields { get; set; }
    public long? Ticks { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>Tails the newest Dedicated_*.log and derives players, activity and performance from it.</summary>
public class LogWatcher(ServerFiles files, ILogger<LogWatcher> logger) : BackgroundService
{
    static readonly Regex Ts = new(@"^(\d\d)-(\d\d):(\d\d):(\d\d)\.(\d{3})\s", RegexOptions.Compiled);
    static readonly Regex InfoRx = new(@"INFO: Uptime=(\S+)\s+\S+\s+\S+\s+heap=\s*(\d+)MB fps=\s*([\d.]+) players=\s*(\d+) pfs=(\S+)(?:\s+ticks=(\d+))?", RegexOptions.Compiled);
    static readonly Regex LoginRx = new(@"\[PA\] Player (\d{17})/'(.*?)' login ok", RegexOptions.Compiled);
    static readonly Regex LogoffRx = new(@"\[CM\] Player (\d{17})/'(.*?)' logged off", RegexOptions.Compiled);
    static readonly Regex PlayfieldRx = new(@"(\d{17})/=/'.*?' connecting now to playfield '(.+?)'", RegexOptions.Compiled);
    static readonly Regex ConnectRx = new(@"Connecting CId=\d+, EId=-?\d+, (\d{17})/=/'.*?' to '(.+?)'", RegexOptions.Compiled);
    static readonly Regex GotIdRx = new(@"Got player id: CId=\d+, EId=(\d+), (\d{17})/", RegexOptions.Compiled);
    static readonly Regex DisconnectRx = new(@"Client CId=\d+, EId=(-?\d+), (\d{17})/=/'.*?' disconnected(?: from (.+?),| \((.+?)\))", RegexOptions.Compiled);
    static readonly Regex FilesRx = new(@"Client CId=\d+, EId=-?\d+, (\d{17})//'.*?' requests (\d+) files", RegexOptions.Compiled);
    readonly Dictionary<string, PlayerHistory> history = new();
    bool historyLoaded, historyDirty;
    string HistoryPath => Path.Combine(files.LogsDir, "Manager", "players.json");
    const int MaxLines = 4000, MaxActivity = 150;

    readonly object sync = new();
    readonly LinkedList<LogLine> lines = new();
    readonly LinkedList<Activity> activity = new();
    readonly Dictionary<string, PlayerState> players = new();
    string? path; long offset; string partial = ""; long seq; DateTime fileStartUtc;

    public ServerInfo Info { get; private set; } = new();
    public bool ConsoleReady { get; private set; }
    public string? CurrentFile { get { lock (sync) return path; } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Poll(); } catch (Exception ex) { logger.LogWarning(ex, "Log poll failed"); }
            try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { }
        }
    }

    void LoadHistory()
    {
        historyLoaded = true;
        try
        {
            if (!File.Exists(HistoryPath)) { BackfillHistory(); return; }
            var list = System.Text.Json.JsonSerializer.Deserialize<List<PlayerHistory>>(File.ReadAllText(HistoryPath)) ?? [];
            foreach (var h in list) history[h.SteamId] = h;
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read player history"); }
    }

    /// <summary>First run only: replay every older Dedicated log (oldest first) to build the player history.</summary>
    void BackfillHistory()
    {
        if (!Directory.Exists(files.LogsDir)) return;
        var logs = new DirectoryInfo(files.LogsDir).EnumerateFiles("Dedicated_*.log", SearchOption.AllDirectories)
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        if (logs.Count > 0) logs.RemoveAt(logs.Count - 1);          // the newest is read by the normal poll
        foreach (var f in logs)
        {
            try
            {
                fileStartUtc = ParseFileStart(f);
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                    if (line.Contains("Player", StringComparison.OrdinalIgnoreCase) || line.Contains("Client CId")) Handle(line);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Backfill failed for {File}", f.Name); }
        }
        // only the history should survive the replay
        lines.Clear(); activity.Clear(); players.Clear(); Info = new ServerInfo(); ConsoleReady = false;
        historyDirty = true;
    }

    void SaveHistory()
    {
        if (!historyDirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(history.Values.OrderBy(h => h.FirstSeen).ToList(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
            historyDirty = false;
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not save player history"); }
    }

    PlayerHistory Hist(string steamId, string? name, DateTimeOffset t)
    {
        if (!history.TryGetValue(steamId, out var h))
            history[steamId] = h = new PlayerHistory { SteamId = steamId, FirstSeen = t };
        if (!string.IsNullOrEmpty(name)) h.Name = name;
        if (t > h.LastSeen) h.LastSeen = t;
        historyDirty = true;
        return h;
    }

    public List<PlayerHistory> GetHistory()
    {
        lock (sync) return history.Values.Select(h => new PlayerHistory
        {
            SteamId = h.SteamId, Name = h.Name, FirstSeen = h.FirstSeen, LastSeen = h.LastSeen, LastLogin = h.LastLogin,
            Visits = h.Visits, EntityId = h.EntityId, LastVisit = h.LastVisit,
        }).OrderByDescending(h => h.LastSeen).ToList();
    }

    void Poll()
    {
        if (!historyLoaded) lock (sync) LoadHistory();
        try { PollLog(); }
        finally { lock (sync) SaveHistory(); }
    }

    void PollLog()
    {
        if (!Directory.Exists(files.LogsDir)) return;
        var latest = new DirectoryInfo(files.LogsDir)
            .EnumerateFiles("Dedicated_*.log", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (latest == null) return;

        lock (sync)
        {
            if (latest.FullName != path)
            {
                path = latest.FullName; offset = 0; partial = "";
                players.Clear(); Info = new ServerInfo(); ConsoleReady = false;
                fileStartUtc = ParseFileStart(latest);
                Add(null, $"── {latest.Name} ──", "system");
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) { offset = 0; partial = ""; }
            if (fs.Length == offset) return;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - offset];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            offset += read;
            var parts = (partial + Encoding.UTF8.GetString(buf, 0, read)).Split('\n');
            partial = parts[^1];
            for (int i = 0; i < parts.Length - 1; i++) Handle(parts[i].TrimEnd('\r'));
        }
    }

    void Handle(string line)
    {
        var m = Ts.Match(line);
        if (!m.Success)
        {
            if (line.Contains("Exception")) Add(null, line, "error");
            return;   // skip Unity's multi-line allocator dumps etc.
        }
        var t = ToLocal(m);
        var kind = line.Contains("-ERR-") || line.Contains("Exception") ? "error" : line.Contains("-WAR-") ? "warn" : "normal";
        Match x;
        if ((x = InfoRx.Match(line)).Success)
        {
            Info = new ServerInfo
            {
                Uptime = x.Groups[1].Value, HeapMB = int.Parse(x.Groups[2].Value),
                Fps = double.Parse(x.Groups[3].Value, CultureInfo.InvariantCulture),
                Players = int.Parse(x.Groups[4].Value), Playfields = x.Groups[5].Value, At = t,
                Ticks = x.Groups[6].Success ? long.Parse(x.Groups[6].Value) : null,
            };
            kind = "routine";
        }
        else if ((x = LoginRx.Match(line)).Success)
        {
            players[x.Groups[1].Value] = new PlayerState { SteamId = x.Groups[1].Value, Name = x.Groups[2].Value, Since = t };
            var h = Hist(x.Groups[1].Value, x.Groups[2].Value, t);
            if (t > h.LastLogin) { h.LastLogin = t; h.Visits++; h.LastVisit = "Connecting…"; }   // re-reading a log never double-counts
            AddActivity(t, "join", $"{x.Groups[2].Value} joined");
            kind = "player";
        }
        else if ((x = LogoffRx.Match(line)).Success)
        {
            players.Remove(x.Groups[1].Value);
            AddActivity(t, "leave", $"{x.Groups[2].Value} left");
            kind = "player";
        }
        else if ((x = PlayfieldRx.Match(line)).Success || (x = ConnectRx.Match(line)).Success)
        {
            if (players.TryGetValue(x.Groups[1].Value, out var p)) p.Playfield = x.Groups[2].Value;
            kind = "player";
        }
        else if ((x = GotIdRx.Match(line)).Success)
        {
            if (players.TryGetValue(x.Groups[2].Value, out var p)) p.EntityId = int.Parse(x.Groups[1].Value);
            var h = Hist(x.Groups[2].Value, null, t);
            h.EntityId = int.Parse(x.Groups[1].Value);
            if (h.LastVisit == "Connecting…" || h.LastVisit == null) h.LastVisit = "In game";
            kind = "player";
        }
        else if ((x = FilesRx.Match(line)).Success)
        {
            var h = Hist(x.Groups[1].Value, null, t);
            if (h.EntityId == null) h.LastVisit = $"Downloading scenario files ({x.Groups[2].Value})";
            kind = "player";
        }
        else if ((x = DisconnectRx.Match(line)).Success)
        {
            var h = Hist(x.Groups[2].Value, null, t);
            h.LastVisit = x.Groups[3].Success ? $"Played on {x.Groups[3].Value}"
                        : x.Groups[4].Value == "not on a playfield" ? "Left while loading" : $"Left ({x.Groups[4].Value})";
            kind = "player";
        }
        else if (line.Contains("Telnet server started"))
        {
            ConsoleReady = true;
            AddActivity(t, "server", "Server started");
        }
        else if (line.Contains("Doing scheduled quit"))
        {
            ConsoleReady = false;
            AddActivity(t, "server", "Server saving and shutting down");
        }
        Add(t, line, kind);
    }

    void Add(DateTimeOffset? t, string text, string kind)
    {
        lines.AddLast(new LogLine(++seq, t, text, kind));
        while (lines.Count > MaxLines) lines.RemoveFirst();
    }

    void AddActivity(DateTimeOffset t, string kind, string text)
    {
        activity.AddFirst(new Activity(t, kind, text));
        while (activity.Count > MaxActivity) activity.RemoveLast();
    }

    DateTimeOffset ToLocal(Match m)
    {
        int day = int.Parse(m.Groups[1].Value);
        var b = fileStartUtc;
        day = Math.Min(day, DateTime.DaysInMonth(b.Year, b.Month));
        var d = new DateTime(b.Year, b.Month, day, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                             int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), DateTimeKind.Utc);
        if (d < b.AddDays(-2)) d = d.AddMonths(1);     // log crossed a month boundary
        return new DateTimeOffset(d).ToLocalTime();
    }

    static DateTime ParseFileStart(FileInfo f)
    {
        var m = Regex.Match(f.Name, @"_(\d{6}-\d{6})");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyMMdd-HHmmss", CultureInfo.InvariantCulture,
                                                DateTimeStyles.AssumeLocal, out var local))
            return local.ToUniversalTime();
        return f.CreationTimeUtc;
    }

    public List<LogLine> GetLines(long after, int max)
    {
        lock (sync) return lines.Where(l => l.Seq > after).TakeLast(max).ToList();
    }

    public List<PlayerState> GetPlayers()
    {
        lock (sync) return players.Values.OrderBy(p => p.Name).Select(p => new PlayerState
            { SteamId = p.SteamId, Name = p.Name, Since = p.Since, Playfield = p.Playfield, EntityId = p.EntityId }).ToList();
    }

    public List<Activity> GetActivity(int max)
    {
        lock (sync) return activity.Take(max).ToList();
    }
}

// ============================================================ background jobs

public class Job
{
    readonly object sync = new();
    readonly List<string> lines = new();
    public string Id { get; } = Guid.NewGuid().ToString("n")[..8];
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public DateTimeOffset? Ended { get; set; }
    public string State { get; set; } = "running";

    public void Add(string line) { lock (sync) lines.Add($"{DateTime.Now:HH:mm:ss}  {line}"); }
    public List<string> Lines(int from) { lock (sync) return lines.Skip(from).ToList(); }
    public int Count { get { lock (sync) return lines.Count; } }
}

/// <summary>Runs one long operation at a time (start, stop, maintenance...) and keeps its output.</summary>
public class JobRunner(ManagerOptions o)
{
    readonly object sync = new();
    public Job? Current { get; private set; }
    public bool Busy => Current?.State == "running";

    public Job Start(string kind, string title, Func<Job, Task> work)
    {
        lock (sync)
        {
            if (Busy) throw new InvalidOperationException($"Wait for \"{Current!.Title}\" to finish first.");
            var job = new Job { Kind = kind, Title = title };
            Current = job;
            _ = Task.Run(async () =>
            {
                try { await work(job); job.State = "succeeded"; job.Add("Done."); }
                catch (Exception ex) { job.Add("FAILED: " + ex.Message); job.State = "failed"; }
                finally { job.Ended = DateTimeOffset.Now; }
            });
            return job;
        }
    }

    /// <summary>Runs Empyrion-Maintenance.ps1 with the given arguments, streaming its output into the job.</summary>
    public Job RunScript(string kind, string title, string args) => Start(kind, title, async job =>
    {
        job.Add($"Running Empyrion-Maintenance.ps1 {args}");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{o.ScriptPath}\" {args} -Settings \"{o.SettingsPath}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = o.ServerDir,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell.");
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) job.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) job.Add("! " + e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"Script exited with code {p.ExitCode} - see the lines above.");
    });

    public static async Task<int> RunAsync(string exe, string args, Job? job = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (job != null) foreach (var l in (await error).Split('\n', StringSplitOptions.RemoveEmptyEntries)) job.Add("! " + l.Trim());
        _ = await output;
        return p.ExitCode;
    }

    public static async Task<string> CaptureAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}

// ============================================================ scheduled tasks

public record TaskInfo(string Key, string Name, string Description, bool Exists, bool Enabled, string Status,
                       string? NextRun, string? LastRun, string? LastResult, string? Schedule, int WarningMinutes,
                       bool UpToDate, string? Problem);

/// <summary>
/// The daily/weekly maintenance tasks in Windows Task Scheduler. Names, folder, days and times come from
/// manager-settings.json; the tasks run scripts\Empyrion-Maintenance.ps1 with -Settings pointing at that file.
/// </summary>
public class TaskService(ManagerOptions o)
{
    public IEnumerable<string> Keys => ["daily", "weekly"];

    string Name(string key) => key == "daily" ? o.Tasks.DailyName : o.Tasks.WeeklyName;
    string Folder => o.Tasks.Folder.EndsWith('\\') ? o.Tasks.Folder : o.Tasks.Folder + "\\";
    string[] Days(string key) => key == "daily" ? o.Maintenance.DailyDays : o.Maintenance.WeeklyDays;
    string Mode(string key) => key == "daily" ? "Daily" : "Weekly";

    public string Description(string key) => key == "daily"
        ? $"Restart, backup{(o.Maintenance.DailyStarterWipe.Length > 0 ? ", starter-system " + o.Maintenance.DailyStarterWipe + " wipe" : "")}{(o.Maintenance.DailySpaceWipe.Length > 0 ? ", " + o.Maintenance.DailySpaceWipe + " wipe in every visited space sector (asteroids)" : "")}{(o.Maintenance.TwiceDaily ? ". Twice a day (every 12 hours)" : "")}"
        : $"Restart, backup{(o.Maintenance.WeeklyWipe.Length > 0 ? ", " + o.Maintenance.WeeklyWipe + " wipe on every visited playfield" : "")}";

    /// <summary>Local time the task fires: the restart time (+ offset hours) minus the longest warning.</summary>
    public string StartTime(int offsetHours = 0)
    {
        var restart = TimeSpan.TryParse(o.Maintenance.RestartTime, CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(4, 0, 0);
        var start = restart + TimeSpan.FromHours(offsetHours) - TimeSpan.FromMinutes(o.Maintenance.LeadMinutes);
        start = TimeSpan.FromMinutes(((start.TotalMinutes % 1440) + 1440) % 1440);
        return $"{(int)start.TotalHours:00}:{start.Minutes:00}";
    }

    /// <summary>The triggers a task should have: (day mask, start HH:mm). Twice-daily adds a second daily run 12h later, every day.</summary>
    List<(int Mask, string Start)> ExpectedTriggers(string key)
    {
        var list = new List<(int, string)>();
        var days = Mask(Days(key));
        if (days != 0) list.Add((days, StartTime()));
        if (key == "daily" && o.Maintenance.TwiceDaily)
        {
            var all = Mask(o.Maintenance.DailyDays.Concat(o.Maintenance.WeeklyDays));
            if (all != 0) list.Add((all, StartTime(12)));
        }
        return list;
    }

    string ExpectedArguments(string key) =>
        $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{o.ScriptPath}\" -Mode {Mode(key)} -Settings \"{o.SettingsPath}\"";

    static readonly string[] FullDays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static int DayIndex(string d) => Array.FindIndex(FullDays, x => x.StartsWith(d, StringComparison.OrdinalIgnoreCase));
    static int Mask(IEnumerable<string> days) => days.Select(DayIndex).Where(i => i >= 0).Aggregate(0, (m, i) => m | (1 << i));
    static string[] DayNames(int mask) => Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => FullDays[i]).ToArray();

    static Task<string> RunPowerShell(string script) =>
        JobRunner.CaptureAsync("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public async Task<List<TaskInfo>> GetAllAsync()
    {
        var result = new List<TaskInfo>();
        foreach (var key in Keys) result.Add(await GetAsync(key));
        return result;
    }

    async Task<TaskInfo> GetAsync(string key)
    {
        var script =
            $"$t = Get-ScheduledTask -TaskPath {Q(Folder)} -TaskName {Q(Name(key))} -ErrorAction SilentlyContinue; " +
            "if ($t) { $i = $t | Get-ScheduledTaskInfo; $a = $t.Actions | Select-Object -First 1; " +
            "[pscustomobject]@{ State = [string]$t.State; " +
            "Next = if ($i.NextRunTime) { $i.NextRunTime.ToString('o') } else { $null }; " +
            "Last = if ($i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { $i.LastRunTime.ToString('o') } else { $null }; " +
            "Result = $i.LastTaskResult; Args = [string]$a.Arguments; " +
            "Triggers = @($t.Triggers | ForEach-Object { [pscustomobject]@{ Days = [int]$_.DaysOfWeek; Start = [string]$_.StartBoundary } }) } | ConvertTo-Json -Compress -Depth 4 }";
        var json = (await RunPowerShell(script)).Trim();
        var lead = o.Maintenance.LeadMinutes;
        if (json.Length == 0)
            return new TaskInfo(key, Name(key), Description(key), false, false, "Not installed", null, null, null, null, lead, false, null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var state = S("State") ?? "";
        var lastResult = r.TryGetProperty("Result", out var res) && res.ValueKind == System.Text.Json.JsonValueKind.Number
            ? res.GetInt64() switch { 0 => "Completed OK", 267011 => null, 267009 => "Running now", var c => $"Exit code {c}" }
            : null;

        var installed = new List<(int Mask, string Start)>();
        if (r.TryGetProperty("Triggers", out var trs))
        {
            var items = trs.ValueKind == System.Text.Json.JsonValueKind.Array ? trs.EnumerateArray().ToList() : [trs];
            foreach (var tr in items)
            {
                var days = tr.TryGetProperty("Days", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.Number ? dd.GetInt32() : 0;
                var start = tr.TryGetProperty("Start", out var ss) && DateTime.TryParse(ss.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at.ToString("HH:mm") : "?";
                installed.Add((days, start));
            }
        }
        string Restart(string start) => TimeSpan.TryParse(start, CultureInfo.InvariantCulture, out var ts)
            ? DateTime.Today.Add(ts).AddMinutes(lead).ToString("HH:mm") : "?";
        var schedule = installed.Count == 0 ? null
            : string.Join(" + ", installed.Select(x => $"{DayList(x.Mask)} · restart {Restart(x.Start)}")) + (installed.Count > 0 ? $" (warnings {lead} min before)" : "");

        // does the installed task match the current settings?
        var problems = new List<string>();
        if (!string.Equals(S("Args"), ExpectedArguments(key), StringComparison.OrdinalIgnoreCase)) problems.Add("runs a different script or settings file");
        var expected = ExpectedTriggers(key);
        if (!expected.OrderBy(x => x.Start).ThenBy(x => x.Mask).SequenceEqual(installed.OrderBy(x => x.Start).ThenBy(x => x.Mask)))
            problems.Add("schedule differs from the settings");
        return new TaskInfo(key, Name(key), Description(key), true, !state.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                            state, S("Next"), S("Last"), lastResult, schedule, lead, problems.Count == 0,
                            problems.Count == 0 ? null : "Needs updating: " + string.Join("; ", problems));
    }

    // Task Scheduler DaysOfWeek bitmask: Sunday = 1 ... Saturday = 64
    static string DayList(int mask)
    {
        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        var picked = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).ToList();
        if (picked.Count == 0) return "No days";
        if (picked.Count == 7) return "Every day";
        var order = picked.Select(i => (i + 6) % 7).OrderBy(i => i).ToList();   // 0 = Mon
        bool consecutive = order.Count > 2 && order.Last() - order.First() == order.Count - 1;
        string N(int monFirst) => names[(monFirst + 1) % 7];
        return consecutive ? $"{N(order.First())}–{N(order.Last())}" : string.Join(", ", order.Select(N));
    }

    /// <summary>Creates or updates the task from the current settings (runs as the current user, while logged on).</summary>
    public async Task InstallAsync(string key)
    {
        if (!Keys.Contains(key)) throw new ArgumentException("Unknown task.");
        if (!File.Exists(o.ScriptPath)) throw new InvalidOperationException($"Maintenance script not found: {o.ScriptPath}");
        var triggers = ExpectedTriggers(key);
        if (triggers.Count == 0) throw new InvalidOperationException("Pick at least one day for this task.");
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument {Q(ExpectedArguments(key))} -WorkingDirectory {Q(o.AppDir)}",
            "$triggers = @()",
        };
        foreach (var (mask, at) in triggers)
        {
            lines.Add($"$tr = New-ScheduledTaskTrigger -Weekly -DaysOfWeek {string.Join(",", DayNames(mask))} -At {Q(at)}");
            lines.Add($"$tr.StartBoundary = (Get-Date {Q(at)}).ToString('yyyy-MM-ddTHH:mm:ss')   # no fixed UTC offset: follows daylight saving");
            lines.Add("$triggers += $tr");
        }
        lines.AddRange([
            "$principal = New-ScheduledTaskPrincipal -UserId \"$env:USERDOMAIN\\$env:USERNAME\" -LogonType Interactive -RunLevel Limited",
            "$set = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries",
            $"Register-ScheduledTask -TaskPath {Q(Folder)} -TaskName {Q(Name(key))} -Action $action -Trigger $triggers -Principal $principal -Settings $set -Description {Q(Description(key))} -Force | Out-Null",
            "'OK'",
        ]);
        var output = (await RunPowerShell(string.Join("\n", lines))).Trim();
        if (!output.EndsWith("OK")) throw new InvalidOperationException("Task Scheduler refused: " + output);
    }

    public async Task RemoveAsync(string key)
    {
        var output = (await RunPowerShell(
            $"Unregister-ScheduledTask -TaskPath {Q(Folder)} -TaskName {Q(Name(key))} -Confirm:$false -ErrorAction Stop; 'OK'")).Trim();
        if (!output.EndsWith("OK")) throw new InvalidOperationException("Couldn't remove the task: " + output);
    }

    public async Task SetEnabledAsync(string key, bool enabled)
    {
        var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var output = (await RunPowerShell($"{verb} -TaskPath {Q(Folder)} -TaskName {Q(Name(key))} -ErrorAction Stop | Out-Null; 'OK'")).Trim();
        if (!output.EndsWith("OK")) throw new InvalidOperationException(output);
    }
}

// ============================================================ backups

public record BackupInfo(string Name, DateTime Created, double SizeMB, string Kind);

public class BackupService(ServerFiles files)
{
    readonly Dictionary<string, (DateTime Stamp, double Size)> sizeCache = new();

    public List<BackupInfo> List()
    {
        if (!Directory.Exists(files.BackupDir)) return [];
        return new DirectoryInfo(files.BackupDir).EnumerateDirectories()
            .Select(d => new BackupInfo(d.Name, d.CreationTime, SizeOf(d), KindOf(d.Name)))
            .OrderByDescending(b => b.Created).ToList();
    }

    static string KindOf(string name) =>
        name.EndsWith("_manual") ? "manual" : name.EndsWith("_pre-restore") ? "pre-restore" : "scheduled";

    double SizeOf(DirectoryInfo d)
    {
        lock (sizeCache)
        {
            if (sizeCache.TryGetValue(d.FullName, out var c) && c.Stamp == d.LastWriteTimeUtc) return c.Size;
            double mb = Math.Round(d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1048576.0, 1);
            sizeCache[d.FullName] = (d.LastWriteTimeUtc, mb);
            return mb;
        }
    }

    public string NewName(string suffix) => $"{files.GameName}_{DateTime.Now:yyyy-MM-dd_HHmm}_{suffix}";

    public static async Task CopyAsync(string from, string to, bool mirror, Job job)
    {
        job.Add($"Copying {from} -> {to}");
        var args = $"\"{from}\" \"{to}\" {(mirror ? "/MIR" : "/E")} /R:2 /W:5 /NFL /NDL /NJH /NJS /NP";
        var code = await JobRunner.RunAsync("robocopy.exe", args, job);
        if (code >= 8) throw new InvalidOperationException($"robocopy failed with exit code {code}.");
    }
}

// ============================================================ tailscale (remote access via Tailscale Serve)

public record TailscaleState(
    bool Installed, bool Running, string? BackendState, string? MachineName, string? DnsName, string? Ip,
    bool MagicDns, bool Served, int Port, string? Url, string? FullUrl, string? Detail);

/// <summary>
/// Exposes the dashboard to the tailnet with `tailscale serve --http=PORT http://127.0.0.1:PORT`.
/// The manager itself keeps listening on 127.0.0.1 only; Tailscale proxies tailnet requests to it.
/// </summary>
public class TailscaleService(ManagerOptions o)
{
    TailscaleState? cached; DateTime cachedAt;
    readonly SemaphoreSlim gate = new(1, 1);

    public int Port => new Uri(o.Url).Port;

    static string? FindCli()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "tailscale.exe");
            if (File.Exists(p)) return p;
        }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(pf) ? pf : null;
    }

    public async Task<TailscaleState> GetAsync(bool fresh = false)
    {
        await gate.WaitAsync();
        try
        {
            if (!fresh && cached != null && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(15)) return cached;
            cached = await ReadAsync(); cachedAt = DateTime.UtcNow;
            return cached;
        }
        finally { gate.Release(); }
    }

    async Task<TailscaleState> ReadAsync()
    {
        var cli = FindCli();
        if (cli == null) return new(false, false, null, null, null, null, false, false, Port, null, null, "Tailscale isn't installed on this PC.");
        try
        {
            using var status = System.Text.Json.JsonDocument.Parse(await JobRunner.CaptureAsync(cli, "status --json --peers=false"));
            var root = status.RootElement;
            var backend = root.TryGetProperty("BackendState", out var b) ? b.GetString() : null;
            var running = backend == "Running";
            string? dns = null, ip = null, machine = null; bool magic = false;
            if (root.TryGetProperty("Self", out var self))
            {
                dns = self.TryGetProperty("DNSName", out var d) ? d.GetString()?.TrimEnd('.') : null;
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0) ip = ips[0].GetString();
                machine = dns?.Split('.')[0];
            }
            if (root.TryGetProperty("CurrentTailnet", out var tn) && tn.ValueKind == System.Text.Json.JsonValueKind.Object &&
                tn.TryGetProperty("MagicDNSEnabled", out var md)) magic = md.GetBoolean();

            var served = false;
            if (running)
            {
                using var serve = System.Text.Json.JsonDocument.Parse(await JobRunner.CaptureAsync(cli, "serve status --json") is { Length: > 0 } s ? s : "{}");
                served = IsServed(serve.RootElement);
            }
            var host = magic && machine != null ? machine : ip;
            return new(true, running, backend, machine, dns, ip, magic, served, Port,
                       host != null ? $"http://{host}:{Port}" : null,
                       dns != null ? $"http://{dns}:{Port}" : null,
                       running ? null : $"Tailscale is {backend ?? "not running"} - sign in to Tailscale on this PC.");
        }
        catch (Exception ex)
        {
            return new(true, false, null, null, null, null, false, false, Port, null, null, "Couldn't read Tailscale status: " + ex.Message);
        }
    }

    bool IsServed(System.Text.Json.JsonElement root)
    {
        // serve config looks like { "TCP": { "8090": { "HTTP": true } }, "Web": { "host:8090": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:8090" } } } } }
        if (!root.TryGetProperty("Web", out var web) || web.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var site in web.EnumerateObject())
        {
            if (!site.Name.EndsWith($":{Port}")) continue;
            if (!site.Value.TryGetProperty("Handlers", out var handlers)) continue;
            foreach (var h in handlers.EnumerateObject())
                if (h.Value.TryGetProperty("Proxy", out var proxy) && (proxy.GetString() ?? "").Contains($":{Port}")) return true;
        }
        return false;
    }

    /// <summary>Turns serving on/off. Returns any message Tailscale printed (e.g. a link to enable Serve on the tailnet).</summary>
    public async Task<string> SetServedAsync(bool on)
    {
        var cli = FindCli() ?? throw new InvalidOperationException("Tailscale isn't installed on this PC.");
        var args = on ? $"serve --bg --http={Port} http://127.0.0.1:{Port}" : $"serve --http={Port} off";
        var psi = new ProcessStartInfo(cli, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not run tailscale.");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        // `serve` can wait for someone to approve Serve on the tailnet - don't hang the dashboard on it
        var finished = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(20000)) is Task t && p.HasExited;
        if (!finished) { try { p.Kill(true); } catch { } }
        var text = ((finished ? await output : "") + "\n" + (finished ? await error : "")).Trim();
        await GetAsync(fresh: true);
        if (!finished) throw new InvalidOperationException("Tailscale is waiting for approval. Check the Tailscale admin console to enable Serve for this tailnet, then try again.");
        if (p.ExitCode != 0) throw new InvalidOperationException(text.Length > 0 ? text : $"tailscale exited with code {p.ExitCode}.");
        return text;
    }
}

// ============================================================ chat history (from the save's global.db)

public record ChatMessage(long Id, long GameTime, DateTimeOffset? Time, int SenderType, int Channel,
                          long SenderEntityId, string? SenderName, long RecipientEntityId, string? RecipientName,
                          long RecipientFactionId, string Text);

/// <summary>A chat entry as shown on the dashboard: either read from the game, or sent from the dashboard.</summary>
public record ChatEntry(string Source, string Channel, DateTimeOffset? Time, string From, string To, string Text);

/// <summary>
/// Reads in-game chat from the ChatMessages table in the save's global.db.
/// The live database is never opened directly (it uses rollback-journal locking, so a reader could block the
/// server's writes). Instead the file is byte-copied without taking SQLite locks whenever it changes,
/// and the copy is queried.
/// </summary>
public class ChatService(ServerFiles files, LogWatcher logs, ILogger<ChatService> logger)
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<ChatMessage> messages = new();
    long lastId; DateTime lastWrite; long lastLength; string? lastSource;
    const int Keep = 300;

    string CopyPath => Path.Combine(Path.GetTempPath(), "EmpyrionManager", "global-chat-copy.db");

    string SentLog => Path.Combine(files.LogsDir, "Manager", "sent-messages.log");

    /// <summary>Remembers a message sent from the dashboard (to "All" or a player's name).</summary>
    public void RecordSent(string to, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentLog)!);
            File.AppendAllText(SentLog, $"{DateTimeOffset.Now:o}\t{to.Replace('\t', ' ')}\t{text.Replace('\t', ' ').Replace('\n', ' ')}\n");
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record sent message"); }
    }

    List<ChatEntry> ReadSent(int max)
    {
        if (!File.Exists(SentLog)) return [];
        var list = new List<ChatEntry>();
        foreach (var line in File.ReadLines(SentLog).TakeLast(max))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            var isPrivate = parts[1] != "All";
            list.Add(new ChatEntry("dashboard", isPrivate ? "private" : "dashboard", at, "Server (dashboard)", parts[1], parts[2]));
        }
        return list;
    }

    /// <summary>Recent chat: game messages from global.db merged with messages sent from the dashboard.</summary>
    public async Task<List<ChatEntry>> GetEntriesAsync(int max = 150)
    {
        await gate.WaitAsync();
        try
        {
            Refresh();
            var game = messages.Select(m =>
            {
                // channel codes seen in global.db: 0 global, 1 faction, 2 alliance, 4 "server" (player -> admins);
                // private messages carry a recipient entity id
                var channel = m.RecipientEntityId > 0 ? "private" : m.Channel switch
                {
                    0 => "global", 1 => "faction", 2 => "alliance", 4 => "server", _ => "other",
                };
                var from = !string.IsNullOrWhiteSpace(m.SenderName) ? m.SenderName!
                         : m.SenderEntityId == 0 ? "Server" : $"Player #{m.SenderEntityId}";
                var to = channel switch
                {
                    "private" => m.RecipientName ?? $"Player #{m.RecipientEntityId}",
                    "global" => "All",
                    "faction" => "Faction",
                    "alliance" => "Alliance",
                    "server" => "Server",
                    _ => $"Channel {m.Channel}",
                };
                return new ChatEntry("game", channel, m.Time, from, to, m.Text);
            });
            return game.Concat(ReadSent(max))
                .OrderBy(e => e.Time ?? DateTimeOffset.MinValue).TakeLast(max).ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<List<ChatMessage>> GetAsync(long after, int max)
    {
        await gate.WaitAsync();
        try
        {
            Refresh();
            return messages.Where(m => m.Id > after).TakeLast(max).ToList();
        }
        finally { gate.Release(); }
    }

    void Refresh()
    {
        var src = Path.Combine(files.GameDir, "global.db");
        var fi = new FileInfo(src);
        if (!fi.Exists) return;
        if (src != lastSource) { messages.Clear(); lastId = 0; lastSource = src; lastWrite = default; }
        if (fi.LastWriteTimeUtc == lastWrite && fi.Length == lastLength) return;

        Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!);
        try
        {
            using (var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = new FileStream(CopyPath, FileMode.Create, FileAccess.Write, FileShare.None))
                from.CopyTo(to);

            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = CopyPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            db.Open();
            using var cmd = db.CreateCommand();
            // sendername is usually null - player names live in Entities, keyed by entity id
            const string select = """
                SELECT c.cmid, c.gametime, c.sendertype, c.channel, c.senderentityid,
                       COALESCE(c.sendername, s.name), c.recentityid, r.name, c.recfacid, c.text
                FROM ChatMessages c
                LEFT JOIN Entities s ON s.entityid = c.senderentityid
                LEFT JOIN Entities r ON r.entityid = c.recentityid
                """;
            cmd.CommandText = lastId == 0
                ? $"SELECT * FROM ({select} ORDER BY c.cmid DESC LIMIT 200) ORDER BY 1"
                : $"{select} WHERE c.cmid > $last ORDER BY c.cmid LIMIT 500";
            cmd.Parameters.AddWithValue("$last", lastId);
            using var r = cmd.ExecuteReader();
            var info = logs.Info;
            while (r.Read())
            {
                long gt = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                DateTimeOffset? when = null;
                // server ticks run at ~20/s; anchor against the latest INFO line (time + ticks) from the log
                if (info.At is { } at && info.Ticks is { } ticks && gt > 0)
                    when = at.AddSeconds((gt - ticks) / 20.0);
                var m = new ChatMessage(r.GetInt64(0), gt, when,
                    r.IsDBNull(2) ? 0 : r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    r.IsDBNull(4) ? 0 : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? 0 : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? 0 : r.GetInt64(8), r.IsDBNull(9) ? "" : r.GetString(9));
                messages.Add(m);
                lastId = Math.Max(lastId, m.Id);
            }
            if (messages.Count > Keep) messages.RemoveRange(0, messages.Count - Keep);
            lastWrite = fi.LastWriteTimeUtc; lastLength = fi.Length;
        }
        catch (Exception ex)
        {
            // a copy taken mid-write can be inconsistent - just try again on the next poll
            logger.LogDebug(ex, "Chat read failed");
        }
    }
}
