<#
.SYNOPSIS
    Triggers one of the personal-mode Scheduled Tasks and shows what it is
    doing, from the desktop user's own session. What the two desktop shortcuts
    actually run.

.DESCRIPTION
    The tasks run as SYSTEM, in session 0, where their console output goes
    nowhere anybody can see. `schtasks /run` returns the moment the task is
    launched, so a shortcut that only did that would flash a window and leave
    the person guessing for the next ninety seconds about whether their
    machine was theirs yet.

    docs/plans/part-time-node.md 4.4 is explicit that this matters more than
    polish: "the user needs to know when the machine is theirs, and a silent
    shortcut means they wait, or don't". So this runs unelevated in the user's
    session, starts the task, and tails the shared log file until the task
    stops running - which turns SYSTEM's invisible output into the visible
    kind without giving the desktop user any privilege it did not already
    have. Reading a log file needs nothing.

    It holds no logic about personal mode itself. Everything it shows was
    decided by Set-PersonalMode.ps1; this only carries it across the session
    boundary.

.PARAMETER TaskName
    The registered task to run, e.g. Aerie-PersonalMode-Enter.

.PARAMETER LogPath
    The log Set-PersonalMode.ps1 appends to. Tailed from wherever it had
    reached when the task started, so previous evenings do not scroll past.

.PARAMETER TimeoutSeconds
    How long to keep watching. Not a deadline on the task - nothing here can
    stop it - only on the watching. Past it this says so and exits, and the
    task carries on.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskName,
    [string]$LogPath = 'C:\ProgramData\Aerie\personal-mode\personal-mode.log',
    [int]$TimeoutSeconds = 900,

    # Seconds to leave the window up after the task finishes, so the last line
    # is readable on a shortcut that closes itself.
    [int]$LingerSeconds = 20
)

$ErrorActionPreference = 'Stop'

$Host.UI.RawUI.WindowTitle = $TaskName

# Where the log had reached before the task was told to start. Everything
# after this offset belongs to this run.
$offset = 0L
if (Test-Path $LogPath -PathType Leaf) {
    try { $offset = (Get-Item $LogPath).Length } catch { $offset = 0L }
}

Write-Host ''
Write-Host "  $TaskName" -ForegroundColor Cyan
Write-Host '  ------------------------------------------------------------'
Write-Host ''

# schtasks rather than Start-ScheduledTask: the run right this user was
# granted is on the task's own security descriptor, and schtasks is the path
# that has always honoured it without needing the ScheduledTasks module to be
# importable in the user's session.
& schtasks.exe /run /tn $TaskName | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Could not start '$TaskName' (schtasks exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host '  An administrator needs to re-run Register-PersonalModeControl.ps1 on this host.' -ForegroundColor Red
    Start-Sleep -Seconds $LingerSeconds
    exit 1
}

$startedAt = Get-Date
$deadline = $startedAt.AddSeconds($TimeoutSeconds)
$sawRunning = $false

while ((Get-Date) -lt $deadline) {
    if (Test-Path $LogPath -PathType Leaf) {
        $length = (Get-Item $LogPath).Length
        if ($length -gt $offset) {
            $stream = [IO.File]::Open($LogPath, 'Open', 'Read', 'ReadWrite')
            try {
                [void]$stream.Seek($offset, 'Begin')
                $reader = New-Object IO.StreamReader($stream)
                $new = $reader.ReadToEnd()
                $offset = $length
            }
            finally { $stream.Dispose() }
            foreach ($line in ($new -split "`r?`n")) {
                if ($line.Trim()) {
                    # The file's own UTC stamp is dropped: the person reading
                    # this wants the step, not the timestamp, and the step
                    # lines already carry their own elapsed seconds.
                    Write-Host ('  ' + ($line -replace '^\S+\s+', ''))
                }
            }
        }
    }

    # `Status: Running` on the query output. Polled rather than waited on
    # because there is no wait primitive here that a standard user can call.
    $query = & schtasks.exe /query /tn $TaskName /fo list 2>$null
    $running = @($query | Where-Object { $_ -match '^\s*Status:\s*Running' }).Count -gt 0
    if ($running) { $sawRunning = $true }
    elseif ($sawRunning -or ((Get-Date) - $startedAt).TotalSeconds -gt 8) {
        # Either it ran and stopped, or it was over before the first poll -
        # an -Exit on a machine that was already in the cluster takes about a
        # second. Waiting on $sawRunning alone would hang that case out to the
        # full timeout with nothing to show.
        break
    }

    Start-Sleep -Milliseconds 700
}

Write-Host ''
if ((Get-Date) -ge $deadline) {
    Write-Host '  Still going after the watch window expired. The task has not been stopped -' -ForegroundColor Yellow
    Write-Host '  it is still working. Re-open this shortcut to keep watching.' -ForegroundColor Yellow
}
Write-Host '  ------------------------------------------------------------'
Write-Host "  This window closes in $LingerSeconds seconds." -ForegroundColor DarkGray
Start-Sleep -Seconds $LingerSeconds
