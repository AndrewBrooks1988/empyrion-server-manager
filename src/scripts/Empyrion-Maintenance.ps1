<#
.SYNOPSIS
    Scheduled maintenance for the Empyrion dedicated server: in-game warnings,
    save + restart, optional backup, then queued playfield wipes.

.DESCRIPTION
    Daily  : warn players -> save & stop -> BACKUP save -> start -> wipe ORE
             DEPOSITS on every visited playfield in the starter systems, and wipe POIs
             (in scenarios like Reforged Eden asteroids are POIs) on every visited SPACE playfield.
    Weekly : warn players -> save & stop -> BACKUP save -> start -> wipe
             POIs + deposits + terrain on EVERY visited playfield.

    Why the restart: the vanilla 'wipe' command is only queued and executes the
    next time a playfield loads. Queuing right after a fresh start (no playfields
    loaded yet) means every wipe applies on the next visit.

    What a wipe touches:
      - 'poi'     resets NPC POIs only - player structures are untouched.
      - 'terrain' regenerates terrain EXCEPT around POIs and player structures (bases).
      - 'deposit' regenerates ore deposits.
    Only playfields that have been visited (have a folder under the save's
    Playfields directory) are wiped; unvisited ones are pristine anyway.

    If the server is NOT running when the task fires, the script does nothing
    (so it never starts a server you deliberately stopped).

    Play   : for the host playing on the same PC. While the server runs, Steam thinks
             the game is already running (the server's steam_appid.txt is the game's
             app ID), so Play in Steam does nothing. This mode warns players (1 min),
             saves & stops the server, launches the game through Steam, waits for it,
             then starts the server again. No backup, no wipes.

    Restart: warn players -> save & stop -> start. No backup, no wipes.
    Stop   : warn players -> save & stop. The server stays off.

.PARAMETER Mode
    Daily, Weekly, Play, Restart or Stop.

.PARAMETER Warn
    Override the countdown, as comma-separated minutes, e.g. "5,1". "0" = no countdown.

.PARAMETER DryRun
    Show what would happen (warnings, wipe commands) without touching the server.

.PARAMETER NoWarning
    Skip the countdown (manual use only, e.g. when nobody is online).

.PARAMETER Settings
    Path to the manager's manager-settings.json (server folder, config file, schedule and wipe
    settings). Defaults to ..\manager-settings.json next to this script's folder.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Empyrion-Maintenance.ps1 -Mode Daily -DryRun
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Daily', 'Weekly', 'Play', 'Restart', 'Stop', 'Start')] [string] $Mode,
    [string] $Warn,
    [string] $Settings,
    [switch] $DryRun,
    [switch] $NoWarning
)

# ================================================================ configuration
# Everything comes from manager-settings.json (edited in the dashboard). The values below are only
# generic fallbacks; nothing server-specific is hard-coded here.
if (-not $Settings) { $Settings = Join-Path (Split-Path $PSScriptRoot -Parent) 'manager-settings.json' }
if (-not (Test-Path $Settings)) { Write-Host "Settings file not found: $Settings - run the manager's setup first."; exit 2 }
$cfgJson = Get-Content $Settings -Raw | ConvertFrom-Json
$m       = $cfgJson.maintenance

function Use($value, $fallback) { if ($null -ne $value -and "$value" -ne '') { $value } else { $fallback } }

$ServerDir      = $cfgJson.serverDir
$ConfigFile     = Use $cfgJson.configFile 'dedicated.yaml'
$LaunchMode     = Use $cfgJson.launchMode '-startDedi'
$WarnAt         = @(Use $m.warnMinutes @(15, 10, 5, 1)) | Sort-Object -Descending
$StarterSystems = @($m.starterSystems | Where-Object { $_ })
$DailyWipe      = "$($m.dailyStarterWipe)".Trim()         # starter systems, visited playfields ('' = none)
$DailySpaceWipe = "$($m.dailySpaceWipe)".Trim()           # every visited SPACE playfield ('' = none)
$DailyOtherWipe = "$($m.dailyOtherWipe)".Trim()           # every visited playfield OUTSIDE the starter systems ('' = none)
$WeeklyWipe     = "$($m.weeklyWipe)".Trim()               # every visited playfield ('' = none)
$SkipTypes      = @(Use $m.skipTypes @('SunRandom', 'SpaceWarpTargetFixed', 'GasGiant'))
$BackupsToKeep  = [int](Use $m.backupsToKeep 14)
$PlayWarnAt     = @([int](Use $m.playWarnMinutes 1))
$SteamGameUri   = Use $cfgJson.steamGameUri 'steam://rungameid/383120'
$ClientProcess  = 'Empyrion'
$UseAlerts      = -not ($cfgJson.useAlerts -eq $false)       # on-screen alerts via the EmpyrionManagerAlerts mod

$ShutdownTimeoutSec = 300
$StartupTimeoutSec  = 900

if (-not $ServerDir -or -not (Test-Path (Join-Path $ServerDir 'EmpyrionLauncher.exe'))) {
    Write-Host "Server folder not set or not an Empyrion dedicated server: '$ServerDir'"; exit 2
}

# ================================================================ logging
$LogDir  = Join-Path $ServerDir 'Logs\Maintenance'
New-Item -ItemType Directory -Force $LogDir | Out-Null
$LogFile = Join-Path $LogDir ("{0:yyyy-MM}.log" -f (Get-Date))

function Write-Log([string] $msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}" -f (Get-Date), $Mode, $msg
    Write-Host $line
    if (-not $DryRun) { Add-Content -Path $LogFile -Value $line }
}

function Get-YamlValue([string] $text, [string] $key) {
    $m = [regex]::Match($text, "(?m)^\s*$key\s*:\s*([^#\r\n]+)")
    if ($m.Success) { $m.Groups[1].Value.Trim().Trim('"', "'") } else { $null }
}

# ================================================================ server config
$cfg      = Get-Content (Join-Path $ServerDir $ConfigFile) -Raw
$telPort  = Get-YamlValue $cfg 'Tel_Port'
$telPwd   = Get-YamlValue $cfg 'Tel_Pwd'
$gameName = Get-YamlValue $cfg 'GameName'
$scenario = Get-YamlValue $cfg 'CustomScenario'
$saveDir  = Get-YamlValue $cfg 'SaveDirectory'
if (-not $saveDir) { $saveDir = 'Saves' }
if (-not $telPort) { $telPort = 30004 }
$gameDir  = Join-Path $ServerDir "$saveDir\Games\$gameName"

# ================================================================ telnet helpers
function Read-Available($stream, [int] $waitMs = 500) {
    Start-Sleep -Milliseconds $waitMs
    $sb  = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 4096
    while ($stream.DataAvailable) {
        $n = $stream.Read($buf, 0, $buf.Length)
        [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $n))
        Start-Sleep -Milliseconds 50
    }
    ($sb.ToString() -replace '\s+', ' ').Trim()
}

# Sends commands in one telnet session; logs each command and the server's reply.
function Send-Telnet([string[]] $commands, [int] $replyWaitMs = 400) {
    if ($DryRun) { $commands | ForEach-Object { Write-Host "  (dry run) > $_" }; return $true }
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', [int]$telPort)
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream)
        $writer.NewLine = "`r`n"; $writer.AutoFlush = $true
        [void](Read-Available $stream 1000)                      # banner / password prompt
        if ($telPwd) { $writer.WriteLine($telPwd); [void](Read-Available $stream 1000) }
        foreach ($cmd in $commands) {
            $writer.WriteLine($cmd)
            $reply = Read-Available $stream $replyWaitMs
            Write-Log ("> $cmd" + $(if ($reply) { " | $reply" }))
        }
        return $true
    }
    catch { Write-Log "Telnet error: $($_.Exception.Message)"; return $false }
    finally { $client.Close() }
}

function Test-Telnet {
    $c = New-Object System.Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', [int]$telPort); $true } catch { $false } finally { $c.Close() }
}

# only the server started from this server folder (another server on the same PC is left alone)
function Get-DediProcess { Get-Process -Name 'EmpyrionDedicated' -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -like "$ServerDir*" } }

function Say([string] $text) { [void](Send-Telnet @("say '$($text -replace "'", '')'")) }

# ---- on-screen alerts (banner + sound) through the EmpyrionManagerAlerts server mod, when it's installed and running
$AlertDir = Join-Path $ServerDir 'Content\Mods\EmpyrionManagerAlerts'
function Test-AlertMod {
    $hb = Join-Path $AlertDir 'heartbeat.txt'
    (Test-Path $hb) -and (((Get-Date).ToUniversalTime() - (Get-Item $hb).LastWriteTimeUtc).TotalSeconds -lt 30)
}
# prio: 0 = red, 1 = yellow, 2 = blue
function Alert([string] $text, [int] $prio = 1, [int] $seconds = 15) {
    if (-not $UseAlerts) { return $false }
    if ($DryRun) { Write-Host "  (dry run) on-screen alert [$prio]: $text"; return $true }
    if (-not (Test-AlertMod)) { return $false }
    $out = Join-Path $AlertDir 'outbox'
    New-Item -ItemType Directory -Force $out | Out-Null
    $name = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $tmp = Join-Path $out "$name.tmp"
    [IO.File]::WriteAllText($tmp, "$prio`n$seconds`nall`n$text", (New-Object Text.UTF8Encoding $false))
    Move-Item $tmp (Join-Path $out "$name.msg")
    Write-Log "On-screen alert [$prio]: $text"
    return $true
}

# ================================================================ wipe targets
function Get-WipeCommands {
    $visited = @(Get-ChildItem (Join-Path $gameDir 'Playfields') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
    if ($visited.Count -eq 0) { return @() }

    if ($Mode -ne 'Daily') {
        if (-not $WeeklyWipe) { return @() }
        return @($visited | ForEach-Object { "wipe '$_' $WeeklyWipe" })
    }

    # Daily: deposits in the starter systems + POIs (incl. asteroids) in every visited SPACE playfield.
    # In RE2 asteroids are POIs. Their own RegenAfter timers are lost whenever an empty sector unloads,
    # so a daily 'poi' wipe is what actually refills them.
    $sectors = Join-Path $gameDir 'Sectors\Sectors.yaml'
    if (-not (Test-Path $sectors)) { $sectors = Join-Path $ServerDir "Content\Scenarios\$scenario\Sectors\Sectors.yaml" }
    $starter = New-Object System.Collections.Generic.HashSet[string]
    $typeOf  = @{}                                       # playfield name -> playfield type (folder in Playfields\)
    $system  = $null
    $pfRegex = "^\s*-\s*\[\s*'[^']*'\s*,\s*(?:'(?<n>[^']*)'|(?<n>[^,]+?))\s*,\s*(?<t>[^,\]]+?)\s*[,\]]"
    foreach ($line in Get-Content $sectors) {
        if ($line -match '^\s*#') { continue }
        if ($line -match '^\s{2}-\s*Name:\s*(.+?)\s*$') { $system = $Matches[1].Trim("'", '"'); continue }
        if ($line -match $pfRegex) {
            $name = $Matches['n'].Trim(); $type = $Matches['t'].Trim().Trim("'", '"')
            $typeOf[$name] = $type
            if ($StarterSystems -contains $system -and $SkipTypes -notcontains $type) { [void]$starter.Add($name) }
        }
    }

    $spaceCache = @{}
    function Test-SpaceType([string] $type) {
        if ($spaceCache.ContainsKey($type)) { return $spaceCache[$type] }
        $dir = Join-Path $ServerDir "Content\Scenarios\$scenario\Playfields\$type"
        $yaml = Get-ChildItem $dir -Filter *.yaml -ErrorAction SilentlyContinue | Select-Object -First 1
        $isSpace = $yaml -and (Select-String -Path $yaml.FullName -Pattern '^\s*PlayfieldType:\s*Space' -Quiet)
        $spaceCache[$type] = [bool]$isSpace
        return [bool]$isSpace
    }

    $commands = foreach ($pf in $visited) {
        $types = @()
        if ($DailyWipe -and $starter.Contains($pf)) { $types += $DailyWipe }
        if ($DailyOtherWipe -and -not $starter.Contains($pf) -and $typeOf.ContainsKey($pf) -and $SkipTypes -notcontains $typeOf[$pf]) { $types += $DailyOtherWipe }
        if ($DailySpaceWipe -and $typeOf.ContainsKey($pf) -and $SkipTypes -notcontains $typeOf[$pf] -and (Test-SpaceType $typeOf[$pf])) { $types += $DailySpaceWipe }
        if ($types.Count) { "wipe '$pf' $(($types -join ' ' -split '\s+' | Where-Object { $_ } | Select-Object -Unique) -join ' ')" }
    }
    @($commands)
}

# ================================================================ main
Write-Log "==== $Mode maintenance started$(if ($DryRun) { ' (DRY RUN)' })"

if ($Mode -eq 'Start') {
    # sign-in task: start the server if it isn't already up (no warnings, no backup, no wipes)
    if (Get-DediProcess) { Write-Log 'Server is already running - nothing to do.'; exit 0 }
    $staged = Join-Path $AlertDir 'EmpyrionManagerAlerts.dll.new'
    if ((Test-Path $staged) -and -not $DryRun) { Move-Item $staged (Join-Path $AlertDir 'EmpyrionManagerAlerts.dll') -Force; Write-Log 'Applied alert mod update' }
    Write-Log "Starting server ($LaunchMode -dedicated $ConfigFile)"
    if (-not $DryRun) {
        Start-Process -FilePath (Join-Path $ServerDir 'EmpyrionLauncher.exe') -WorkingDirectory $ServerDir `
                      -ArgumentList $LaunchMode, '-dedicated', $ConfigFile
        $deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
        while (-not (Test-Telnet) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
        if (-not (Test-Telnet)) { Write-Log "WARNING: server not answering Telnet after $StartupTimeoutSec s (it may still be loading)."; exit 1 }
        Write-Log 'Server is up'
    }
    Write-Log "==== $Mode finished"
    exit 0
}

if ($Mode -eq 'Play') {
    if (Get-Process -Name $ClientProcess -ErrorAction SilentlyContinue) { Write-Log 'Game is already running - nothing to do.'; exit 0 }
    if (-not (Get-DediProcess)) {
        Write-Log 'Server is not running - just launching the game through Steam.'
        if (-not $DryRun) { Start-Process $SteamGameUri }
        exit 0
    }
    $WarnAt = $PlayWarnAt
}
elseif (-not (Get-DediProcess) -and -not $DryRun) {
    Write-Log 'Server is not running - skipping (nothing restarted, no wipes queued).'
    exit 0
}

if ($Warn) {
    $WarnAt = @($Warn -split ',' | ForEach-Object { [int]$_.Trim() } | Where-Object { $_ -gt 0 } | Sort-Object -Descending)
    if ($WarnAt.Count -eq 0) { $NoWarning = $true }
}

# ---- 1. countdown warnings
$what = switch ($Mode) {
    'Weekly'  { 'Weekly reset (POIs, ore deposits and terrain regenerate - bases are safe)' }
    'Play'    { 'Quick server restart (about 2 minutes down)' }
    'Restart' { 'Server restart (a few minutes down)' }
    'Stop'    { 'Server shutdown' }
    default   { 'Daily restart (starter system ore deposits regenerate)' }
}
if (-not $NoWarning) {
    for ($i = 0; $i -lt $WarnAt.Count; $i++) {
        $m = $WarnAt[$i]
        Say "[SERVER] $what in $m minute$(if ($m -ne 1) { 's' }). Get somewhere safe and log off before then."
        [void](Alert "$what in $m minute$(if ($m -ne 1) { 's' }). Get somewhere safe." $(if ($m -le 1) { 0 } elseif ($m -le 5) { 1 } else { 2 }) 15)
        $next = if ($i + 1 -lt $WarnAt.Count) { $WarnAt[$i + 1] } else { 0 }
        if (-not $DryRun) { Start-Sleep -Seconds (($m - $next) * 60) }
    }
}
if ($Mode -eq 'Stop') { Say '[SERVER] Saving and shutting down NOW.'; $alerted = Alert 'Server shutting down NOW.' 0 10 }
else { Say '[SERVER] Saving and restarting NOW. Back in a few minutes.'; $alerted = Alert 'Server restarting NOW. Back in a few minutes.' 0 10 }
if ($alerted -and -not $DryRun) { Start-Sleep -Seconds 3 }      # let the mod deliver it before the server goes down

# ---- 2. save and shut down
Write-Log 'Sending saveandexit'
[void](Send-Telnet @('saveandexit 0') 2000)
if (-not $DryRun) {
    $deadline = (Get-Date).AddSeconds($ShutdownTimeoutSec)
    while ((Get-DediProcess) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
    if (Get-DediProcess) { Write-Log "ERROR: server still running after $ShutdownTimeoutSec s - aborting (not killing it, check manually)."; exit 1 }
    Start-Sleep -Seconds 10                                   # let playfield processes finish writing
    Get-Process -Name 'EmpyrionPlayfieldServer' -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -like "$ServerDir*" } |
        ForEach-Object { Write-Log "Waiting on playfield process $($_.Id)"; $_.WaitForExit(60000) | Out-Null }
    Write-Log 'Server stopped cleanly'
    # apply an alert-mod update staged while the server had the old DLL loaded
    $staged = Join-Path $AlertDir 'EmpyrionManagerAlerts.dll.new'
    if (Test-Path $staged) { Move-Item $staged (Join-Path $AlertDir 'EmpyrionManagerAlerts.dll') -Force; Write-Log 'Applied alert mod update' }
}
if ($Mode -eq 'Stop') { Write-Log "==== $Mode finished (server left off)"; exit 0 }

# ---- 3a. Play mode: start the game through Steam BEFORE the server comes back,
#          so Steam registers the real client as "the game"
if ($Mode -eq 'Play') {
    Write-Log "Launching the game through Steam ($SteamGameUri)"
    if (-not $DryRun) {
        Start-Process $SteamGameUri
        $deadline = (Get-Date).AddSeconds(180)
        while (-not (Get-Process -Name $ClientProcess -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
        if (Get-Process -Name $ClientProcess -ErrorAction SilentlyContinue) { Write-Log 'Game client is running' }
        else { Write-Log 'WARNING: game client not detected after 180 s - starting the server anyway' }
        Start-Sleep -Seconds 30                               # let the client finish its Steam login
    }
}

# ---- 3b. backup (daily and weekly runs, while the save is not in use)
$backupRoot = Join-Path $ServerDir 'Backups'
$dest = Join-Path $backupRoot ("{0}_{1:yyyy-MM-dd_HHmm}" -f $gameName, (Get-Date))
$doBackup = $Mode -in 'Daily', 'Weekly'
if ($doBackup) { Write-Log "Backing up save to $dest" }
if (-not $DryRun -and $doBackup) {
    New-Item -ItemType Directory -Force $backupRoot | Out-Null
    robocopy $gameDir $dest /E /R:2 /W:5 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Write-Log "WARNING: backup robocopy exit code $LASTEXITCODE" }
    # only prune scheduled backups (Name_yyyy-MM-dd_HHmm) - manual / pre-restore copies are kept
    $autoName = '^' + [regex]::Escape($gameName) + '_\d{4}-\d{2}-\d{2}_\d{4}$'
    Get-ChildItem $backupRoot -Directory | Where-Object Name -match $autoName |
        Sort-Object Name -Descending | Select-Object -Skip $BackupsToKeep |
        ForEach-Object { Write-Log "Removing old backup $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }
}

# ---- 4. start the server again
Write-Log "Starting server ($LaunchMode -dedicated $ConfigFile)"
if (-not $DryRun) {
    Start-Process -FilePath (Join-Path $ServerDir 'EmpyrionLauncher.exe') -WorkingDirectory $ServerDir `
                  -ArgumentList $LaunchMode, '-dedicated', $ConfigFile
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
    while (-not (Test-Telnet) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
    if (-not (Test-Telnet)) { Write-Log "ERROR: server did not come back up within $StartupTimeoutSec s - wipes NOT queued."; exit 1 }
    Start-Sleep -Seconds 20                                   # let the dedi finish initialising
    Write-Log 'Server is back up'
}

# ---- 5. queue wipes
if ($Mode -in 'Play', 'Restart') { Write-Log "==== $Mode finished"; exit 0 }
$commands = Get-WipeCommands
if ($commands.Count -eq 0) { Write-Log 'No matching visited playfields - nothing to wipe.' }
else {
    Write-Log "Queuing $($commands.Count) wipe command(s)"
    if (-not (Send-Telnet $commands)) { Write-Log 'ERROR: failed to queue wipes.'; exit 1 }
}

Write-Log "==== $Mode maintenance finished"
