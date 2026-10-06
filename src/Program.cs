using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EmpyrionManager;

// Content and web roots sit next to the exe, so the app works from any working directory.
var exeDir = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir,
    WebRootPath = Path.Combine(exeDir, "wwwroot"),
});

// manager-settings.json (created by the setup page). Falls back to appsettings.json's "Manager" section.
var options = ManagerOptions.Load(exeDir, builder.Configuration);
builder.WebHost.UseUrls(options.Url);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ServerFiles>();
builder.Services.AddSingleton<TelnetService>();
builder.Services.AddSingleton<ProcessMonitor>();
builder.Services.AddSingleton<LogWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogWatcher>());
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<TaskService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<TailscaleService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ServerConfigService>();
builder.Services.AddSingleton<GameOptionsService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<ScenarioService>();
builder.Services.AddSingleton<SteamNames>();
builder.Services.AddSingleton<DecayService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
ProcessMonitor.ServerDir = () => options.ServerDir;
var webRoot = app.Environment.WebRootPath;

// ---------------------------------------------------------------- optional dashboard password
// Requests made on this PC (http://127.0.0.1 / localhost) never need it. Anything else (LAN, Tailscale) does,
// but only when a password has been set in Settings -> Setup.
var sessions = new ConcurrentDictionary<string, DateTimeOffset>();
bool IsLocal(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) &&
    ctx.Request.Host.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
bool IsAuthed(HttpContext ctx) =>
    options.AccessPasswordHash.Length == 0 || IsLocal(ctx) ||
    (ctx.Request.Cookies.TryGetValue("mgr_session", out var token) && sessions.TryGetValue(token, out var exp) && exp > DateTimeOffset.UtcNow);

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (IsAuthed(ctx) || path is "/login" or "/api/login" || path.StartsWith("/app.css")) { await next(); return; }
    if (path.StartsWith("/api")) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Sign in required." }); return; }
    ctx.Response.Redirect("/login");
});

app.MapGet("/login", async ctx =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(Path.Combine(webRoot, "login.html"));
});
app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    await Task.Delay(400);                                       // slow down guessing
    if (body == null || !body.TryGetValue("password", out var pwd) || !options.CheckPassword(pwd))
        return Results.Json(new { error = "Wrong password." }, statusCode: 401);
    var token = ManagerOptions.RandomSecret(40);
    sessions[token] = DateTimeOffset.UtcNow.AddDays(30);
    ctx.Response.Cookies.Append("mgr_session", token, new CookieOptions
        { HttpOnly = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
    return Results.Json(new { ok = true });
});

// ---------------------------------------------------------------- dashboard page
// Served with a version stamp on its script/style links (from the files' write times), so browsers fetch
// fresh copies after every update instead of reusing a cached one.
string AssetVersion() => string.Join("", new[] { "app.js", "app.css" }
    .Select(f => File.GetLastWriteTimeUtc(Path.Combine(webRoot, f)).Ticks.ToString("x")));
app.MapGet("/", async ctx =>
{
    var html = (await File.ReadAllTextAsync(Path.Combine(webRoot, "index.html")))
        .Replace("__V__", AssetVersion())
        .Replace("__TITLE__", WebUtility.HtmlEncode(options.Title));
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html);
});
app.MapGet("/index.html", ctx => { ctx.Response.Redirect("/"); return Task.CompletedTask; });

// Everything else: allow caching, but always revalidate (cheap 304 when unchanged)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    await next();
});
Api.Map(app);
SettingsApi.Map(app);

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"{options.Title} running at {options.Url}");
    Console.WriteLine(options.Configured ? $"Server folder: {options.ServerDir}" : "Not set up yet - open the dashboard to finish setup.");
    Console.WriteLine("Close this window to stop the manager (the game server keeps running).");
    // open the dashboard (not when started by the updater/autostart with --no-browser)
    if (options.OpenBrowserOnStart && !args.Contains("--no-browser"))
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(options.Url.Replace("0.0.0.0", "127.0.0.1")) { UseShellExecute = true }); }
        catch { /* no default browser - fine */ }
    }
});
app.Run();
