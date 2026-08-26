<#
.SYNOPSIS
    Hands the part-time host back to the person sitting at it, and takes it
    back afterwards. Drains the node, stops the runner, stops the VM - and the
    reverse. docs/plans/part-time-node.md Phase 4.

.DESCRIPTION
    One machine in this cluster belongs to someone. A few evenings a month
    they want all of it, and this is the thing that gives it to them.

    **The asymmetry is the design, and everything else follows from it.**

      Entering personal mode may not fail. It is a promise to a person, not a
      negotiation with a cluster. Every step has a deadline, and past that
      deadline the sequence moves on regardless - an undrained pod is an
      ordinary node failure, which the cluster survives by design and which
      Phase 2 made survivable on purpose. A step that could block would mean a
      person waiting on Longhorn to decide something.

      Leaving personal mode is allowed to fail loudly. Nothing is waiting on
      it, the cluster is running without this node already, and a half-rejoined
      node reported as a failure is better than one silently cordoned forever.

    So Enter reports what each step managed inside its deadline and then keeps
    going, and its exit code answers exactly one question: **is the machine
    actually the person's now** - VM off, runner stopped. Not "did the drain
    finish".

    ## Actions

      -Enter      Give the machine back. Label, cordon, drain, wait out the
                  runner, stop the runner service, stop the VM, write state.
      -Exit       Take it back. Start the runner service, start the VM, wait
                  for Ready, uncordon, remove the label, clear state.
      -Status     Read everything and change nothing. Safe from any shell.
      -Reconcile  What the boot-time task runs. Reads the state file and makes
                  the machine match it - which is what makes a Windows Update
                  reboot harmless in both directions (finding 6).
      -AutoExit   What the daily task runs. -Exit unless the pin is set.
      -Pin        Hold personal mode through the daily auto-exit.
      -Unpin      Release it.

    ## Why the state file exists

    The VM's `AutomaticStartAction` is `Nothing` (finding 6), so Hyper-V will
    not bring this node back on its own after a host reboot - deliberately,
    because a Windows Update reboot in the middle of a gaming evening would
    otherwise return the node with nobody having asked for it, and
    Set-UpdateRebootSchedule.ps1 sets NoAutoRebootWithLoggedOnUsers = 0 so that
    reboot goes straight through an active session.

    Something still has to decide, and Hyper-V cannot read an intention. The
    state file is that intention written down, and -Reconcile at boot is what
    reads it. Personal mode on: leave everything down, and stop the runner
    service again, because it is Automatic and Windows will have started it.
    Personal mode off: bring the node back.

    ## What this talks to, and why it needs a key

    The node is a k3s **agent**. It has no kubeconfig and no apiserver, so
    `k3s kubectl` on it talks to a default localhost:8080 and fails - the same
    thing Phase 1.3 discovered. Every cluster-level operation here is therefore
    asked of a permanent **server** over SSH, which is why registration puts a
    private key on this host. Nothing in this script needs cluster credentials
    of its own.

    ## Idempotent, in both directions

    Enter on a machine already in personal mode re-asserts it rather than
    erroring - which is the state a Reconcile leaves behind, and the state a
    second click produces. Exit on a machine already in the cluster likewise.

.PARAMETER ConfigPath
    Written once by Register-PersonalModeControl.ps1 and read here, so the
    Scheduled Task's command line is short and a value can be changed without
    re-registering two tasks and two shortcuts. Every value in it can be
    overridden by the matching parameter below.

.PARAMETER DrainTimeoutSeconds
    Step 2's deadline, and the number the plan says is a guess until 2.4's
    rehearsal measures it. Past it, the sequence proceeds with pods still on
    the node - which is an ordinary node failure and not this script's problem
    to solve.

.EXAMPLE
    # What the desktop shortcut runs, through a Scheduled Task.
    .\Set-PersonalMode.ps1 -Enter

.EXAMPLE
    # Safe from any shell, elevated or not, at any time.
    .\Set-PersonalMode.ps1 -Status
#>
#Requires -Modules Hyper-V
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Enter')][switch]$Enter,
    [Parameter(Mandatory, ParameterSetName = 'Exit')][switch]$Exit,
    [Parameter(Mandatory, ParameterSetName = 'Status')][switch]$Status,
    [Parameter(Mandatory, ParameterSetName = 'Reconcile')][switch]$Reconcile,
    [Parameter(Mandatory, ParameterSetName = 'AutoExit')][switch]$AutoExit,
    [Parameter(Mandatory, ParameterSetName = 'Pin')][switch]$Pin,
    [Parameter(Mandatory, ParameterSetName = 'Unpin')][switch]$Unpin,

    [string]$ConfigPath = 'C:\ProgramData\Aerie\personal-mode\config.json',

    # Every one of these defaults from the config file. Passed here only to
    # override it for one run - which is mostly a debugging affordance.
    [string]$VMName,
    [string]$NodeName,
    [string]$ServerAddress,
    [string]$NodeAddress,
    [string]$Username,
    [string]$SshPrivateKeyPath,

    [int]$DrainTimeoutSeconds = 90,
    [int]$RunnerDrainTimeoutSeconds = 300,
    [int]$VmStopTimeoutSeconds = 120,
    [int]$NodeReadyTimeoutSeconds = 420,

    # Bounds every cluster call that is not the drain. Short on purpose: these
    # are one-shot API writes, and a slow one is a broken one.
    [int]$ClusterCallTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\AerieSsh.ps1')

# Phase 5 reads this label off the Node object to decide whether an absence is
# expected. It goes on *before* the drain, while the API server is still
# reachable and the node is still Ready, and it survives the node going
# NotReady because it lives on the Node object rather than on anything the
# kubelet has to keep alive - which is the whole reason the exclusion can be
# expressed as a label at all.
$PersonalModeLabel = 'aerie.family/personal-mode'

$StateDir = Split-Path -Parent $ConfigPath
$StatePath = Join-Path $StateDir 'state.json'
$LogPath = Join-Path $StateDir 'personal-mode.log'

$script:StartedUtc = (Get-Date).ToUniversalTime()
$script:Steps = New-Object Collections.Generic.List[psobject]

function Write-Line {
    param([string]$Message, [string]$Colour = 'Gray')
    $stamp = (Get-Date).ToString('HH:mm:ss')
    Write-Host "[$stamp] $Message" -ForegroundColor $Colour
    try {
        if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
        Add-Content -Path $LogPath -Value "$((Get-Date).ToUniversalTime().ToString('o'))  $Message" -ErrorAction SilentlyContinue
    }
    catch {
        # A log that cannot be written is not a reason to stop handing the
        # machine back. Nothing above this line depends on it.
    }
}

function Add-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('done', 'deadline', 'skipped', 'failed')][string]$Result,
        [Parameter(Mandatory)][double]$Seconds,
        [string]$Detail = ''
    )
    $script:Steps.Add([pscustomobject]@{ Step = $Name; Result = $Result; Seconds = [math]::Round($Seconds, 1); Detail = $Detail })
    $colour = switch ($Result) { 'done' { 'Green' } 'skipped' { 'DarkGray' } 'deadline' { 'Yellow' } default { 'Red' } }
    Write-Line ("  {0,-28} {1,-9} {2,6}s  {3}" -f $Name, $Result, [math]::Round($Seconds, 1), $Detail) $colour
}

# ------------------------------------------------------------------ #
# The deadline, as a mechanism rather than as an intention.
# ------------------------------------------------------------------ #
#
# "Every step has a deadline and proceeds past it" is only true if something
# can actually cut a step off. kubectl's own --timeout bounds the drain, but
# nothing bounds an ssh whose TCP connection is established and whose remote
# command never returns - which is exactly the shape of a cluster that is
# unwell, and exactly when a person is waiting.
#
# So work that must be bounded runs in a background job and the job is stopped
# at the deadline. Stopping the job kills ssh.exe with it. The cost is one
# process start per call, about a second, which is a fair price for the
# difference between a promise and a hope.
function Invoke-WithDeadline {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [object[]]$ArgumentList = @()
    )
    $job = Start-Job -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
    $completed = Wait-Job -Job $job -Timeout $TimeoutSeconds
    if (-not $completed) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ TimedOut = $true; Output = ''; ExitCode = $null }
    }
    $output = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    # A job's output is whatever the block emitted; the convention below is
    # that every block's last statement is the pscustomobject from
    # Invoke-NodeSsh, so the last emitted object is the answer.
    $last = if ($output.Count -gt 0) { $output[-1] } else { $null }
    [pscustomobject]@{
        TimedOut = $false
        ExitCode = if ($last -and $last.PSObject.Properties['ExitCode']) { $last.ExitCode } else { $null }
        Output   = if ($last -and $last.PSObject.Properties['StdOut']) { "$($last.StdOut)$($last.StdErr)" } else { ($output -join "`n") }
    }
}

# One remote kubectl, bounded. -Command carries no double quotes, which
# Invoke-NodeSsh refuses outright - see its own note on why they cannot
# survive the trip.
function Invoke-ClusterCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )
    $lib = Join-Path $PSScriptRoot 'lib\AerieSsh.ps1'
    Invoke-WithDeadline -TimeoutSeconds $TimeoutSeconds -ArgumentList @($lib, $cfg.ServerAddress, $cfg.Username, $cfg.SshPrivateKeyPath, $knownHostsFile, $Command) -ScriptBlock {
        param($lib, $ip, $user, $key, $known, $cmd)
        . $lib
        Invoke-NodeSsh -IPAddress $ip -User $user -KeyPath $key -KnownHostsFile $known -Command $cmd
    }
}

# `kubectl get node --no-headers` gives NAME STATUS ROLES AGE VERSION, and
# STATUS is what everything here keys off - `Ready`, `Ready,SchedulingDisabled`,
# `NotReady`. Parsed through a guard rather than by index, because under
# Set-StrictMode an out-of-bounds index is a terminating error, and a truncated
# line from a node mid-restart is exactly the input that produces one.
function Get-NodeStatusWord {
    param([string]$NoHeaderLine)
    $fields = @($NoHeaderLine -split '\s+' | Where-Object { $_ })
    if ($fields.Count -lt 2) { return $null }
    return $fields[1]
}

function Get-RunnerServices {
    @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue)
}

function Read-State {
    if (Test-Path $StatePath -PathType Leaf) {
        try {
            $raw = Get-Content $StatePath -Raw | ConvertFrom-Json
            # Normalised rather than returned as parsed. Under
            # Set-StrictMode, reading a key an older (or hand-edited) state
            # file does not carry is a terminating error - which would make a
            # missing field stop a boot reconcile, and the whole point of that
            # task is that it always reaches a decision.
            return [pscustomobject]@{
                mode   = if ($raw.PSObject.Properties['mode'] -and $raw.mode) { $raw.mode } else { 'cluster' }
                since  = if ($raw.PSObject.Properties['since']) { $raw.since } else { $null }
                pinned = [bool]($raw.PSObject.Properties['pinned'] -and $raw.pinned)
                by     = if ($raw.PSObject.Properties['by']) { $raw.by } else { $null }
            }
        }
        catch {
            Write-Line "State file at $StatePath does not parse; treating this machine as being in the cluster." 'Yellow'
        }
    }
    # The absence of a state file means the cluster owns the machine. That is
    # the safe default in the one direction it matters: a host that boots with
    # no state file brings its node back, rather than sitting out of the
    # cluster indefinitely because a file went missing.
    [pscustomobject]@{ mode = 'cluster'; since = $null; pinned = $false; by = $null }
}

function Write-State {
    param([Parameter(Mandatory)][string]$Mode, [bool]$Pinned = $false, [string]$By)
    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    $state = [ordered]@{
        mode   = $Mode
        since  = (Get-Date).ToUniversalTime().ToString('o')
        pinned = $Pinned
        by     = if ($By) { $By } else { "$env:USERDOMAIN\$env:USERNAME" }
        host   = $env:COMPUTERNAME
        vmName = $cfg.VMName
    }
    $state | ConvertTo-Json | Set-Content -Path $StatePath -Encoding UTF8
}

# ------------------------------------------------------------------ #
# Configuration
# ------------------------------------------------------------------ #

$fileConfig = if (Test-Path $ConfigPath -PathType Leaf) { Get-Content $ConfigPath -Raw | ConvertFrom-Json } else { $null }

function Get-ConfigValue {
    param([string]$Override, [string]$Key, [string]$Default)
    if ($Override) { return $Override }
    if ($fileConfig -and $fileConfig.PSObject.Properties[$Key] -and $fileConfig.$Key) { return $fileConfig.$Key }
    return $Default
}

$cfg = [pscustomobject]@{
    VMName            = Get-ConfigValue $VMName            'vmName'            'aerie-node-3'
    NodeName          = Get-ConfigValue $NodeName          'nodeName'          ''
    ServerAddress     = Get-ConfigValue $ServerAddress     'serverAddress'     ''
    NodeAddress       = Get-ConfigValue $NodeAddress       'nodeAddress'       ''
    Username          = Get-ConfigValue $Username          'username'          'aerie'
    SshPrivateKeyPath = Get-ConfigValue $SshPrivateKeyPath 'sshPrivateKeyPath' (Join-Path $StateDir 'node.key')
}
if (-not $cfg.NodeName) { $cfg.NodeName = $cfg.VMName }

$knownHostsFile = Join-Path $StateDir 'known_hosts'

# The cluster half is optional in exactly one place - -Status, which should
# answer as much as it can from any shell rather than refusing to run.
$clusterReachable = [bool]($cfg.ServerAddress -and (Test-Path $cfg.SshPrivateKeyPath -PathType Leaf))
if (-not $clusterReachable -and $PSCmdlet.ParameterSetName -in @('Enter', 'Exit', 'Reconcile', 'AutoExit')) {
    throw "No cluster access configured. Expected a server address and a private key at '$($cfg.SshPrivateKeyPath)'. Run Register-PersonalModeControl.ps1 once, as an administrator, to write $ConfigPath and install the key."
}

# ------------------------------------------------------------------ #
# The two directions
# ------------------------------------------------------------------ #

function Invoke-Enter {
    Write-Line "Handing $env:COMPUTERNAME back. Node '$($cfg.NodeName)', VM '$($cfg.VMName)'." 'Cyan'
    Write-Line 'No step here can stop the sequence. Each has a deadline and the next one runs regardless.' 'DarkGray'

    # --- 1. the label, before the drain --------------------------------- #
    # First on purpose. Phase 5's alert rules and the status page read it to
    # tell "off by request" from "down", and they have to be able to read it
    # by the time the node stops being Ready - which the drain begins.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
        "sudo k3s kubectl label node $($cfg.NodeName) $PersonalModeLabel=true --overwrite --request-timeout=${ClusterCallTimeoutSeconds}s"
    $sw.Stop()
    if ($r.TimedOut) { Add-Step 'label personal-mode' 'deadline' $sw.Elapsed.TotalSeconds 'the node will look down rather than lent out until it returns' }
    elseif ($r.ExitCode -eq 0) { Add-Step 'label personal-mode' 'done' $sw.Elapsed.TotalSeconds "$PersonalModeLabel=true" }
    else { Add-Step 'label personal-mode' 'failed' $sw.Elapsed.TotalSeconds ($r.Output -replace '\s+', ' ') }

    # --- 2. cordon, then drain ------------------------------------------ #
    # Two calls rather than one: cordon is instant and is what stops new work
    # arriving, and it is worth having landed even if the drain then hits its
    # deadline. Longhorn's node controller also watches for the cordon - it is
    # what makes it drop the per-node instance-manager PDB that would
    # otherwise block the drain - so the earlier it lands the better.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
        "sudo k3s kubectl cordon $($cfg.NodeName) --request-timeout=${ClusterCallTimeoutSeconds}s"
    $sw.Stop()
    if ($r.TimedOut) { Add-Step 'cordon' 'deadline' $sw.Elapsed.TotalSeconds 'new pods may still schedule here until the node goes NotReady' }
    elseif ($r.ExitCode -eq 0) { Add-Step 'cordon' 'done' $sw.Elapsed.TotalSeconds 'unschedulable' }
    else { Add-Step 'cordon' 'failed' $sw.Elapsed.TotalSeconds ($r.Output -replace '\s+', ' ') }

    # No --force. A pod no controller owns would be deleted with nothing to
    # recreate it, and this runs on a Friday night without a person reading
    # the output. The deadline is the answer to a drain that will not finish,
    # not a bigger hammer.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-ClusterCommand -TimeoutSeconds ($DrainTimeoutSeconds + 15) -Command `
        "sudo k3s kubectl drain $($cfg.NodeName) --ignore-daemonsets --delete-emptydir-data --timeout=${DrainTimeoutSeconds}s"
    $sw.Stop()
    if ($r.TimedOut -or $r.ExitCode -ne 0) {
        $detail = if ($r.TimedOut) { 'no answer inside the deadline' } else { ($r.Output -replace '\s+', ' ') }
        # Deliberately not a failure. Pods still on this node when it goes
        # away are an ordinary node failure, which is the thing Phase 2 spent
        # its whole length making survivable.
        Add-Step 'drain' 'deadline' $sw.Elapsed.TotalSeconds "proceeding anyway - $detail"
    }
    else {
        Add-Step 'drain' 'done' $sw.Elapsed.TotalSeconds 'every evictable pod moved'
    }

    # --- 3. let the runner finish what it started ----------------------- #
    # Polling for the absence of a Runner.Worker process, not asking GitHub.
    # Local, credential-free, and exact: the listener process is always there,
    # a worker exists only while a job is running. Re-checked after each
    # disappearance in case the listener picked up another one.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds($RunnerDrainTimeoutSeconds)
    $sawJob = $false
    $quietSince = $null
    while ((Get-Date) -lt $deadline) {
        $workers = @(Get-Process -Name 'Runner.Worker' -ErrorAction SilentlyContinue)
        if ($workers.Count -gt 0) {
            $sawJob = $true
            $quietSince = $null
            Start-Sleep -Seconds 3
            continue
        }
        if (-not $quietSince) { $quietSince = Get-Date; Start-Sleep -Seconds 5; continue }
        # Five seconds clear of the last worker exiting. The listener starts
        # the next job within about a second of finishing one, so a single
        # empty reading is not an idle runner.
        if (((Get-Date) - $quietSince).TotalSeconds -ge 5) { break }
        Start-Sleep -Seconds 2
    }
    $sw.Stop()
    $stillRunning = @(Get-Process -Name 'Runner.Worker' -ErrorAction SilentlyContinue).Count
    if ($stillRunning -gt 0) {
        # Every workflow that runs on this host is a re-runnable dispatch, so
        # a killed job costs a re-run and nothing else.
        Add-Step 'runner finishes job' 'deadline' $sw.Elapsed.TotalSeconds "$stillRunning job(s) still running - they will be cut off; re-dispatch them"
    }
    elseif ($sawJob) { Add-Step 'runner finishes job' 'done' $sw.Elapsed.TotalSeconds 'waited out a running job' }
    else { Add-Step 'runner finishes job' 'done' $sw.Elapsed.TotalSeconds 'idle' }

    # --- 4. stop the runner service ------------------------------------- #
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $services = Get-RunnerServices
    if ($services.Count -eq 0) {
        Add-Step 'stop runner service' 'skipped' $sw.Elapsed.TotalSeconds 'no actions.runner.* service on this host'
    }
    else {
        $stopped = 0
        foreach ($svc in $services) {
            try { Stop-Service -Name $svc.Name -Force -ErrorAction Stop; $stopped++ }
            catch { Write-Line "  could not stop $($svc.Name): $($_.Exception.Message)" 'Red' }
        }
        $sw.Stop()
        if ($stopped -eq $services.Count) { Add-Step 'stop runner service' 'done' $sw.Elapsed.TotalSeconds "$stopped service(s)" }
        else { Add-Step 'stop runner service' 'failed' $sw.Elapsed.TotalSeconds "$stopped of $($services.Count) stopped" }
    }

    # --- 5. stop the VM ------------------------------------------------- #
    # Three escalating attempts, in the order that costs the guest least.
    #
    # The guest is asked in its own language first. Hyper-V's ACPI shutdown
    # depends on the guest running the shutdown integration service, and this
    # is a cloud image rather than a machine anybody configured - so `systemctl
    # poweroff` over SSH is both more likely to work and cleaner when it does.
    # Stop-VM is the fallback, and a hard turn-off is what the deadline buys.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $vm = Get-VM -Name $cfg.VMName -ErrorAction SilentlyContinue
    if (-not $vm) {
        Add-Step 'stop VM' 'failed' $sw.Elapsed.TotalSeconds "no VM named '$($cfg.VMName)' on this host"
    }
    elseif ($vm.State -eq 'Off') {
        Add-Step 'stop VM' 'skipped' $sw.Elapsed.TotalSeconds 'already off'
    }
    else {
        $how = ''
        if ($cfg.NodeAddress) {
            $lib = Join-Path $PSScriptRoot 'lib\AerieSsh.ps1'
            $r = Invoke-WithDeadline -TimeoutSeconds 20 -ArgumentList @($lib, $cfg.NodeAddress, $cfg.Username, $cfg.SshPrivateKeyPath, $knownHostsFile) -ScriptBlock {
                param($lib, $ip, $user, $key, $known)
                . $lib
                # systemd tears the session down before sshd can report an
                # exit status, so a non-zero exit here means nothing. The VM
                # state below is the only reading that counts.
                Invoke-NodeSsh -IPAddress $ip -User $user -KeyPath $key -KnownHostsFile $known -Command 'sudo systemctl poweroff'
            }
            if (-not $r.TimedOut) { $how = 'guest poweroff' }
        }
        $waitUntil = (Get-Date).AddSeconds([math]::Min(45, $VmStopTimeoutSeconds))
        while ((Get-Date) -lt $waitUntil -and (Get-VM -Name $cfg.VMName).State -ne 'Off') { Start-Sleep -Seconds 2 }

        if ((Get-VM -Name $cfg.VMName).State -ne 'Off') {
            $how = 'ACPI shutdown'
            Stop-VM -Name $cfg.VMName -Force -ErrorAction SilentlyContinue
            $waitUntil = (Get-Date).AddSeconds([math]::Max(15, $VmStopTimeoutSeconds - $sw.Elapsed.TotalSeconds))
            while ((Get-Date) -lt $waitUntil -and (Get-VM -Name $cfg.VMName).State -ne 'Off') { Start-Sleep -Seconds 2 }
        }

        if ((Get-VM -Name $cfg.VMName).State -ne 'Off') {
            # Past the deadline the machine is the person's, and a VM that
            # will not shut down does not get to hold it. -TurnOff is the
            # power cable; the node comes back with an unclean filesystem
            # journal to replay and nothing worse, because nothing on this
            # node holds a Longhorn replica (finding 3).
            $how = 'turned off'
            Stop-VM -Name $cfg.VMName -TurnOff -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
        }
        $sw.Stop()

        if ((Get-VM -Name $cfg.VMName).State -eq 'Off') { Add-Step 'stop VM' 'done' $sw.Elapsed.TotalSeconds $how }
        else { Add-Step 'stop VM' 'failed' $sw.Elapsed.TotalSeconds "still $((Get-VM -Name $cfg.VMName).State) after every escalation - the memory is NOT back" }
    }

    # --- 6. write the state file ---------------------------------------- #
    # Last, so that a run interrupted halfway leaves the state saying
    # `cluster` and the boot task therefore puts the node back - the safe
    # direction to be wrong in, since it costs a person one click rather than
    # costing the cluster a node it thinks it has.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pinned = (Read-State).pinned
    Write-State -Mode 'personal' -Pinned $pinned
    $sw.Stop()
    Add-Step 'write state' 'done' $sw.Elapsed.TotalSeconds "personal$(if ($pinned) { ', pinned' })"

    $vmOff = ((Get-VM -Name $cfg.VMName -ErrorAction SilentlyContinue).State -eq 'Off')
    $runnersStopped = @(Get-RunnerServices | Where-Object { $_.Status -ne 'Stopped' }).Count -eq 0
    return ($vmOff -and $runnersStopped)
}

function Invoke-Exit {
    Write-Line "Taking $env:COMPUTERNAME back into the cluster. Node '$($cfg.NodeName)', VM '$($cfg.VMName)'." 'Cyan'
    Write-Line 'This direction is allowed to fail: nothing is waiting on it, and a half-rejoined node is worth reporting.' 'DarkGray'
    $failures = 0

    # --- 1. the runner service ------------------------------------------ #
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $services = Get-RunnerServices
    if ($services.Count -eq 0) { Add-Step 'start runner service' 'skipped' $sw.Elapsed.TotalSeconds 'no actions.runner.* service on this host' }
    else {
        $started = 0
        foreach ($svc in $services) {
            try { Start-Service -Name $svc.Name -ErrorAction Stop; $started++ }
            catch { Write-Line "  could not start $($svc.Name): $($_.Exception.Message)" 'Red' }
        }
        $sw.Stop()
        if ($started -eq $services.Count) { Add-Step 'start runner service' 'done' $sw.Elapsed.TotalSeconds "$started service(s)" }
        else { $failures++; Add-Step 'start runner service' 'failed' $sw.Elapsed.TotalSeconds "$started of $($services.Count) started" }
    }

    # --- 2. the VM ------------------------------------------------------- #
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $vm = Get-VM -Name $cfg.VMName -ErrorAction SilentlyContinue
    if (-not $vm) { $failures++; Add-Step 'start VM' 'failed' $sw.Elapsed.TotalSeconds "no VM named '$($cfg.VMName)' on this host" }
    elseif ($vm.State -eq 'Running') { Add-Step 'start VM' 'skipped' $sw.Elapsed.TotalSeconds 'already running' }
    else {
        try { Start-VM -Name $cfg.VMName -ErrorAction Stop; $sw.Stop(); Add-Step 'start VM' 'done' $sw.Elapsed.TotalSeconds 'started' }
        catch { $failures++; $sw.Stop(); Add-Step 'start VM' 'failed' $sw.Elapsed.TotalSeconds $_.Exception.Message }
    }

    # --- 3. wait for the node to be Ready -------------------------------- #
    # Uncordoning a node the kubelet has not registered as Ready yet is a
    # write the API server accepts and the scheduler then acts on, sending
    # pods at a node that is still booting. So the wait is a step rather than
    # a courtesy.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    $deadline = (Get-Date).AddSeconds($NodeReadyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
            "sudo k3s kubectl get node $($cfg.NodeName) --no-headers --request-timeout=${ClusterCallTimeoutSeconds}s"
        if (-not $r.TimedOut -and $r.ExitCode -eq 0) {
            # "aerie-node-3   Ready,SchedulingDisabled   <none>   5d   v1.33.x"
            $status = Get-NodeStatusWord $r.Output
            if ($status -and ($status -split ',')[0] -eq 'Ready') { $ready = $true; break }
        }
        Start-Sleep -Seconds 10
    }
    $sw.Stop()
    if ($ready) { Add-Step 'node Ready' 'done' $sw.Elapsed.TotalSeconds 'the kubelet registered' }
    else { $failures++; Add-Step 'node Ready' 'failed' $sw.Elapsed.TotalSeconds "still not Ready after ${NodeReadyTimeoutSeconds}s - the steps below ran anyway, so the node rejoins on its own if it is only slow" }

    # --- 4. uncordon ------------------------------------------------------ #
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
        "sudo k3s kubectl uncordon $($cfg.NodeName) --request-timeout=${ClusterCallTimeoutSeconds}s"
    $sw.Stop()
    if (-not $r.TimedOut -and $r.ExitCode -eq 0) { Add-Step 'uncordon' 'done' $sw.Elapsed.TotalSeconds 'schedulable' }
    else { $failures++; Add-Step 'uncordon' 'failed' $sw.Elapsed.TotalSeconds "$(if ($r.TimedOut) { 'no answer inside the deadline' } else { $r.Output -replace '\s+', ' ' }) - the node is back but nothing will schedule on it until this succeeds" }

    # --- 5. remove the label ---------------------------------------------- #
    # After the uncordon, not before: while the label is on, Phase 5 is
    # suppressing this node's alerts, and the last moment worth suppressing
    # them is the moment it is genuinely back.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
        "sudo k3s kubectl label node $($cfg.NodeName) $PersonalModeLabel- --request-timeout=${ClusterCallTimeoutSeconds}s"
    $sw.Stop()
    if (-not $r.TimedOut -and $r.ExitCode -eq 0) { Add-Step 'remove label' 'done' $sw.Elapsed.TotalSeconds "$PersonalModeLabel gone" }
    else { $failures++; Add-Step 'remove label' 'failed' $sw.Elapsed.TotalSeconds "$(if ($r.TimedOut) { 'no answer inside the deadline' } else { $r.Output -replace '\s+', ' ' }) - this node's absences stay silent until it is removed" }

    # --- 6. clear the state ----------------------------------------------- #
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-State -Mode 'cluster' -Pinned $false
    $sw.Stop()
    Add-Step 'write state' 'done' $sw.Elapsed.TotalSeconds 'cluster, pin cleared'

    return ($failures -eq 0)
}

function Get-StatusReport {
    $state = Read-State
    $vm = Get-VM -Name $cfg.VMName -ErrorAction SilentlyContinue
    $services = Get-RunnerServices
    $workers = @(Get-Process -Name 'Runner.Worker' -ErrorAction SilentlyContinue).Count

    $nodeStatus = 'not asked'
    $nodeLabel = 'not asked'
    if ($clusterReachable) {
        $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
            "sudo k3s kubectl get node $($cfg.NodeName) --no-headers --request-timeout=${ClusterCallTimeoutSeconds}s"
        $nodeStatus = if ($r.TimedOut) { 'no answer inside the deadline' }
        elseif ($r.ExitCode -ne 0) { "unreadable: $($r.Output -replace '\s+', ' ')" }
        else { $w = Get-NodeStatusWord $r.Output; if ($w) { $w } else { 'unreadable' } }

        $r = Invoke-ClusterCommand -TimeoutSeconds $ClusterCallTimeoutSeconds -Command `
            "sudo k3s kubectl get node $($cfg.NodeName) -o jsonpath={.metadata.labels} --request-timeout=${ClusterCallTimeoutSeconds}s"
        $nodeLabel = if (-not $r.TimedOut -and $r.ExitCode -eq 0 -and $r.Output -match [regex]::Escape($PersonalModeLabel)) { 'present' } else { 'absent' }
    }

    [pscustomobject]@{
        Host          = $env:COMPUTERNAME
        Mode          = $state.mode
        Since         = $state.since
        Pinned        = $state.pinned
        By            = $state.by
        VM            = if ($vm) { "$($cfg.VMName): $($vm.State)" } else { "$($cfg.VMName): not on this host" }
        RunnerService = if ($services.Count -eq 0) { 'none registered' } else { (($services | ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ', ') }
        RunnerJobs    = $workers
        Node          = "$($cfg.NodeName): $nodeStatus"
        PersonalLabel = $nodeLabel
    }
}

# ------------------------------------------------------------------ #
# Dispatch
# ------------------------------------------------------------------ #

$succeeded = $true

switch ($PSCmdlet.ParameterSetName) {
    'Enter' {
        $succeeded = Invoke-Enter
    }

    'Exit' {
        $succeeded = Invoke-Exit
    }

    'Reconcile' {
        # The boot task (4.5). Finding 6 in one function: the VM's start
        # action is Nothing, so the question "should this node be running"
        # arrives at every boot with no answer attached, and this is where the
        # answer is read rather than guessed.
        $state = Read-State
        Write-Line "Boot reconcile: the state file says '$($state.mode)'." 'Cyan'
        if ($state.mode -eq 'personal') {
            # Windows started the runner service on the way up - it is
            # Automatic, and nothing told it otherwise. Personal mode means
            # the machine is the person's, including its CPU.
            $running = @(Get-RunnerServices | Where-Object { $_.Status -ne 'Stopped' })
            foreach ($svc in $running) {
                try { Stop-Service -Name $svc.Name -Force -ErrorAction Stop; Write-Line "  stopped $($svc.Name), which Windows had started at boot." }
                catch { Write-Line "  could not stop $($svc.Name): $($_.Exception.Message)" 'Red' }
            }
            $vm = Get-VM -Name $cfg.VMName -ErrorAction SilentlyContinue
            if ($vm -and $vm.State -ne 'Off') {
                Write-Line "  VM is $($vm.State) despite personal mode - stopping it." 'Yellow'
                Stop-VM -Name $cfg.VMName -Force -ErrorAction SilentlyContinue
            }
            Write-Line 'Personal mode holds through the reboot. Nothing was returned to the cluster.' 'Green'
        }
        else {
            Write-Line 'The cluster owns this machine, so the node goes back up.' 'Cyan'
            $succeeded = Invoke-Exit
        }
    }

    'AutoExit' {
        # The daily task (4.6). Forgetting to give the node back should cost
        # one night, not one month.
        $state = Read-State
        if ($state.mode -ne 'personal') {
            Write-Line 'Auto-exit: already in the cluster, nothing to do.' 'DarkGray'
        }
        elseif ($state.pinned) {
            Write-Line 'Auto-exit: the pin is set, so personal mode stays. Clear it with -Unpin.' 'Yellow'
        }
        else {
            Write-Line "Auto-exit: personal mode has been on since $($state.since). Taking the node back." 'Cyan'
            $succeeded = Invoke-Exit
        }
    }

    'Pin' {
        $state = Read-State
        Write-State -Mode $state.mode -Pinned $true -By $state.by
        Write-Line 'Pinned. The daily auto-exit will leave this machine alone until -Unpin.' 'Green'
    }

    'Unpin' {
        $state = Read-State
        Write-State -Mode $state.mode -Pinned $false -By $state.by
        Write-Line 'Unpinned. The daily auto-exit will take the node back at its next run.' 'Green'
    }

    default {
        Get-StatusReport | Format-List | Out-String -Width 200 | ForEach-Object { Write-Host $_.TrimEnd() }
        if (-not $clusterReachable) {
            Write-Host ''
            Write-Host "No cluster access is configured, so the node's own state was not read. Run Register-PersonalModeControl.ps1 as an administrator to set it up." -ForegroundColor Yellow
        }
    }
}

if ($script:Steps.Count -gt 0) {
    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $script:StartedUtc).TotalSeconds, 1)
    Write-Host ''
    $script:Steps | Format-Table -AutoSize | Out-String -Width 200 | ForEach-Object { Write-Host $_.TrimEnd() }
    Write-Host ''
    if ($PSCmdlet.ParameterSetName -eq 'Enter') {
        if ($succeeded) {
            Write-Host "This machine is yours. All of it, in ${elapsed}s." -ForegroundColor Green
            Write-Host 'Run the "Aerie: rejoin the cluster" shortcut when you are done.' -ForegroundColor Green
        }
        else {
            Write-Host "The sequence finished in ${elapsed}s but the machine was NOT fully handed back - see the failed step above." -ForegroundColor Red
        }
    }
    elseif ($succeeded) { Write-Host "Back in the cluster, in ${elapsed}s." -ForegroundColor Green }
    else { Write-Host "Rejoin FAILED after ${elapsed}s. The steps above say which part; re-running is safe." -ForegroundColor Red }
}

if (-not $succeeded) { exit 1 }
