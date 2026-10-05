using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmpyrionManager;

public record UpdateState(
    string Current, string? Latest, bool Available, string? Notes, string? ReleaseUrl, string? PublishedAt,
    bool Installed, bool CanInstall, DateTimeOffset? CheckedAt, string? Error, bool Busy, string Repo);

/// <summary>
/// Checks GitHub Releases for a newer version and installs it.
///
/// Releases carry the Inno Setup installer plus a ".sig" file. The signature is made with the project's private
/// update key (kept outside the repo) and checked here against the public key below, so only releases signed
/// by the maintainer are ever installed. This is the same idea as Tauri's updater key.
/// </summary>
public class UpdateService(ManagerOptions o, IConfiguration config, ILogger<UpdateService> logger, IHostApplicationLifetime life) : BackgroundService
{
    /// <summary>Public half of the update signing key (tools\UpdateSigner genkey). Forks: replace with your own.</summary>
    const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtBCeT7l7BUekqJQgXR4sy1VLgmAf
        zZGh3b1QomCzMqLtIM38l8eUHBmIGpkMP11UTPO3h2/jSjSOO9ixnWgxNw==
        -----END PUBLIC KEY-----
        """;

    static readonly HttpClient Http = CreateClient();
    readonly SemaphoreSlim gate = new(1, 1);
    string? installerUrl, sigUrl, latest, notes, releaseUrl, published, error;
    DateTimeOffset? checkedAt;
    bool busy;

    public string Repo => config["Updates:Repo"] is { Length: > 0 } r ? r : "AndrewBrooks1988/empyrion-server-manager";
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    /// <summary>Installed with the setup program (so an update can replace it in place)?</summary>
    public bool Installed => File.Exists(Path.Combine(o.AppDir, "unins000.exe"));

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"EmpyrionServerManager/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    bool NewerAvailable => latest != null && Version.TryParse(latest, out var l) && Version.TryParse(CurrentVersion, out var c) && l > c;

    public UpdateState State => new(CurrentVersion, latest, NewerAvailable, notes, releaseUrl, published,
        Installed, Installed && NewerAvailable && installerUrl != null && sigUrl != null, checkedAt, error, busy, Repo);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (TaskCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (o.CheckForUpdates) await CheckAsync();
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (TaskCanceledException) { return; }
        }
    }

    public async Task<UpdateState> CheckAsync()
    {
        await gate.WaitAsync();
        try
        {
            using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { error = "No releases published yet."; latest = null; }
            else
            {
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                latest = (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                notes = r.TryGetProperty("body", out var b) ? b.GetString() : null;
                releaseUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                published = r.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                installerUrl = sigUrl = null;
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (name.StartsWith("EmpyrionServerManager-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) installerUrl = url;
                    if (name.StartsWith("EmpyrionServerManager-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe.sig", StringComparison.OrdinalIgnoreCase)) sigUrl = url;
                }
                error = null;
            }
        }
        catch (Exception ex) { error = "Couldn't check for updates: " + ex.Message; logger.LogDebug(ex, "Update check failed"); }
        finally { checkedAt = DateTimeOffset.Now; gate.Release(); }
        return State;
    }

    /// <summary>Downloads the installer, verifies its signature, runs it silently and exits (the installer restarts the manager).</summary>
    public async Task InstallAsync()
    {
        if (!Installed) throw new InvalidOperationException("This copy wasn't installed with the setup program. Download the installer from the release page instead.");
        await CheckAsync();
        if (!NewerAvailable || installerUrl == null || sigUrl == null) throw new InvalidOperationException("No signed update is available.");
        await gate.WaitAsync();
        try
        {
            busy = true;
            var dir = Path.Combine(Path.GetTempPath(), "EmpyrionManagerUpdate");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, $"EmpyrionServerManager-Setup-{latest}.exe");
            await File.WriteAllBytesAsync(exe, await Http.GetByteArrayAsync(installerUrl));
            var sig = (await Http.GetStringAsync(sigUrl)).Trim();

            using var key = ECDsa.Create();
            key.ImportFromPem(PublicKeyPem);
            var valid = key.VerifyData(await File.ReadAllBytesAsync(exe), Convert.FromBase64String(sig),
                                       HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!valid)
            {
                File.Delete(exe);
                throw new InvalidOperationException("The downloaded installer's signature doesn't match. The update was NOT installed.");
            }

            logger.LogInformation("Installing update {Version}", latest);
            Process.Start(new ProcessStartInfo(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            _ = Task.Run(async () => { await Task.Delay(800); life.StopApplication(); });   // let the response reach the browser
        }
        finally { busy = false; gate.Release(); }
    }
}
