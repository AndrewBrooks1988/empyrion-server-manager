using System.Security.Cryptography;
using System.Text;

namespace EmpyrionManager;

/// <summary>
/// On-screen alerts (the coloured banner at the top of the screen with sound) via the bundled dedicated-server mod
/// "EmpyrionManagerAlerts". The manager installs the mod into the server's Content\Mods, then sends alerts by dropping
/// small *.msg files into the mod's outbox; the mod shows them in game and deletes them.
/// </summary>
public class AlertService(ManagerOptions o)
{
    public const string ModName = "EmpyrionManagerAlerts";
    static readonly string[] ModFiles = [$"{ModName}.dll", $"{ModName}_Info.yaml"];

    string BundledDir => Path.Combine(o.AppDir, "mod", ModName);
    public string ModDir => Path.Combine(o.ServerDir, "Content", "Mods", ModName);
    string Outbox => Path.Combine(ModDir, "outbox");
    string Heartbeat => Path.Combine(ModDir, "heartbeat.txt");

    public bool Bundled => File.Exists(Path.Combine(BundledDir, ModFiles[0]));
    public bool Installed => File.Exists(Path.Combine(ModDir, ModFiles[0]));

    /// <summary>The mod is loaded in the running server (it refreshes its heartbeat every ~5 s).</summary>
    public bool Active => File.Exists(Heartbeat) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Heartbeat) < TimeSpan.FromSeconds(30)
                          && ProcessMonitor.IsRunning("EmpyrionDedicated");

    public bool UpToDate => Installed && Bundled && Hash(Path.Combine(ModDir, ModFiles[0])) == Hash(Path.Combine(BundledDir, ModFiles[0]));

    static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

    public object State => new
    {
        bundled = Bundled, installed = Installed, active = Active, upToDate = UpToDate, modDir = ModDir,
        useForWarnings = o.UseAlerts,
        lastHeartbeat = File.Exists(Heartbeat) ? File.GetLastWriteTime(Heartbeat) : (DateTime?)null,
    };

    /// <summary>Copies the bundled mod into the server's Content\Mods. The server loads it on its next start.</summary>
    public void Install()
    {
        if (!o.ServerDirValid) throw new InvalidOperationException("Set the server folder first.");
        if (!Bundled) throw new InvalidOperationException($"The alert mod isn't included in this build ({BundledDir}).");
        Directory.CreateDirectory(ModDir);
        foreach (var f in ModFiles)
        {
            var dest = Path.Combine(ModDir, f);
            try { File.Copy(Path.Combine(BundledDir, f), dest, true); }
            catch (IOException) when (f.EndsWith(".dll"))
            {
                // the running server has the old DLL loaded: stage it, it's swapped in when the server is stopped
                File.Copy(Path.Combine(BundledDir, f), dest + ".new", true);
            }
        }
    }

    /// <summary>Applies a staged DLL update (only possible while the server is stopped).</summary>
    public void ApplyStaged()
    {
        var staged = Path.Combine(ModDir, ModFiles[0] + ".new");
        if (File.Exists(staged) && !ProcessMonitor.IsRunning("EmpyrionDedicated")) File.Move(staged, Path.Combine(ModDir, ModFiles[0]), true);
    }

    public void Uninstall()
    {
        if (ProcessMonitor.IsRunning("EmpyrionDedicated")) throw new InvalidOperationException("Stop the server first. It has the mod loaded.");
        if (Directory.Exists(ModDir)) Directory.Delete(ModDir, true);
    }

    /// <summary>Queues an on-screen alert. prio: 0 red, 1 yellow, 2 blue. target: "all", "player:&lt;entityId&gt;", "faction:&lt;id&gt;".</summary>
    public void Send(string text, int prio = 1, double seconds = 10, string target = "all")
    {
        if (!Active) throw new InvalidOperationException("On-screen alerts need the alert mod running on the server (Settings → Setup → On-screen alerts).");
        text = text.Replace("\r", " ").Trim();
        if (text.Length == 0) throw new ArgumentException("Type a message first.");
        Directory.CreateDirectory(Outbox);
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..30];
        var tmp = Path.Combine(Outbox, name + ".tmp");
        File.WriteAllText(tmp, $"{Math.Clamp(prio, 0, 2)}\n{Math.Clamp(seconds, 2, 60).ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{target}\n{text}", new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(Outbox, name + ".msg"));     // atomic: the mod never sees a half-written file
    }
}
