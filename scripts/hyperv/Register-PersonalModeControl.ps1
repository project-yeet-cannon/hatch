<#
.SYNOPSIS
    Installs the personal-mode control on the part-time host: four Scheduled
    Tasks, two desktop shortcuts, a configuration file and the SSH key the
    control needs. Run once, by an administrator.
    docs/plans/part-time-node.md 4.3-4.6.

.DESCRIPTION
    **The whole reason this exists is the privilege boundary.** Handing the
    machine back means draining a node, stopping a Windows service and
    stopping a VM - three things that need Administrator. The person who wants
    their machine back is sitting at a desktop, is not an administrator, and
    should not meet a UAC prompt every time they want to play a game.

    A Scheduled Task registered by an administrator, running as SYSTEM, with
    run rights granted to that user, is the mechanism Windows provides for
    exactly this: the *action* is privileged, the *trigger* is not, and the
    task definition is the contract between them. The desktop user can start
    these two tasks and can do nothing else with them - not change what they
    run, not change who they run as.

    ## What gets installed

    Under `-InstallPath` (C:\ProgramData\Aerie\personal-mode by default):

      bin\                  Set-PersonalMode.ps1 and the library it needs, copied
                            out of the repo. Copied rather than referenced,
                            for the same reason Register-VmConsoleLogShipper.ps1
                            copies its shipper next to the VM: the repo
                            checkout is a runner workspace that gets cleaned,
                            moved and re-cloned, and a Scheduled Task pointing
                            into one is a task that stops working on a day
                            nobody connects to this.
      config.json           Which VM, which node, which server to ask, which
                            key to use. Read by every action.
      state.json            The intention. Written by Enter/Exit, read by the
                            boot task. This is the file finding 6 turns on.
      node.key              The SSH private key, ACL'd to SYSTEM and
                            Administrators only.
      personal-mode.log     Appended by every run; tailed by the shortcuts.

    Four tasks, all SYSTEM at highest privilege:

      Aerie-PersonalMode-Enter     On demand. The desktop user may run it.
      Aerie-PersonalMode-Exit      On demand. The desktop user may run it.
      Aerie-PersonalMode-Boot      AtStartup. Reads the state file and makes
                                   the machine match it (4.5).
      Aerie-PersonalMode-AutoExit  Daily. Gives the node back unless pinned
                                   (4.6).

    And two shortcuts on the desktop, each opening a console that shows the
    steps as they happen (4.4) - via lib\Watch-PersonalModeTask.ps1, which
    exists because a SYSTEM task's console output is in session 0 where
    nobody can see it.

    ## Idempotent

    Re-running replaces every task, rewrites the config, re-copies the scripts
    and re-creates the shortcuts. It does **not** touch state.json - so
    re-registering while somebody is mid-evening does not take their machine
    away from them. That is the one file this script will not write.

.PARAMETER DesktopUser
    Who may enter and leave personal mode without being an administrator.
    DOMAIN\user, .\user, or a bare name resolved against this machine. This is
    the only identity that gets any right here, and the only rights it gets
    are read and execute on two tasks.

.PARAMETER ServerAddress
    A permanent k3s **server**'s LAN address. The node on this host is an
    agent - it has no kubeconfig and no apiserver of its own - so every
    cluster operation is asked of a server over SSH.

.PARAMETER SshPrivateKey
    The key content, for a caller holding it in a variable rather than a file.
    Written to node.key with an ACL of SYSTEM and Administrators, and nothing
    else. The desktop user never needs to read it: they trigger a task, and
    SYSTEM does the reading.

.PARAMETER AutoExitHour
    Local hour for the daily auto-exit (4.6). Default 5am - late enough that
    nobody is still gaming, early enough that a forgotten evening costs one
    night rather than one month.

.EXAMPLE
    # The one-time install, in an elevated session on the part-time host.
    .\Register-PersonalModeControl.ps1 `
        -DesktopUser 'HOSTNAME\someone' `
        -VMName aerie-node-3 -NodeAddress 192.168.1.243 `
        -ServerAddress 192.168.1.240 `
        -SshPrivateKeyPath C:\keys\aerie_node

.EXAMPLE
    # What it looks like afterwards, from any shell.
    C:\ProgramData\Aerie\personal-mode\bin\Set-PersonalMode.ps1 -Status
#>
#Requires -Modules Hyper-V, ScheduledTasks
#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'KeyPath')]
param(
    [Parameter(Mandatory)]
    [string]$DesktopUser,

    [Parameter(Mandatory)]
    [string]$VMName,

    # Defaults to the VM name, which is what Provision 0 makes the guest
    # hostname and therefore what the Node object is called.
    [string]$NodeName,

    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$ServerAddress,

    # The node's own address. Optional, and used for one thing: asking the
    # guest to power itself off in its own language before Hyper-V is asked to
    # do it for them. Worth having - ACPI shutdown depends on a guest
    # integration service that a cloud image may or may not be running.
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$NodeAddress,

    [string]$Username = 'aerie',

    [Parameter(Mandatory, ParameterSetName = 'KeyPath')]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$SshPrivateKeyPath,

    [Parameter(Mandatory, ParameterSetName = 'KeyLiteral')]
    [string]$SshPrivateKey,

    [string]$InstallPath = 'C:\ProgramData\Aerie\personal-mode',

    [ValidateRange(0, 23)]
    [int]$AutoExitHour = 5,

    # Where the shortcuts go. The Public desktop by default, so they are there
    # for whoever logs in rather than for whoever happened to be logged in
    # when this was run.
    [string]$DesktopPath = (Join-Path $env:PUBLIC 'Desktop'),

    # Print what would be installed and stop. Everything here is a write, so
    # this is the way to read the plan first.
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $NodeName) { $NodeName = $VMName }

$TaskEnter = 'Aerie-PersonalMode-Enter'
$TaskExit = 'Aerie-PersonalMode-Exit'
$TaskBoot = 'Aerie-PersonalMode-Boot'
$TaskAutoExit = 'Aerie-PersonalMode-AutoExit'

$binPath = Join-Path $InstallPath 'bin'
$libPath = Join-Path $binPath 'lib'
$configPath = Join-Path $InstallPath 'config.json'
$keyPath = Join-Path $InstallPath 'node.key'
$logPath = Join-Path $InstallPath 'personal-mode.log'
$controlScript = Join-Path $binPath 'Set-PersonalMode.ps1'
$watchScript = Join-Path $libPath 'Watch-PersonalModeTask.ps1'

function Write-Stage {
    param([Parameter(Mandatory)][string]$Name)
    Write-Host ''
    Write-Host "== $Name " -NoNewline -ForegroundColor Cyan
    Write-Host ('=' * [math]::Max(0, 60 - $Name.Length)) -ForegroundColor Cyan
}

# ---------------------------------------------------------------- #
Write-Stage 'Preflight'
# ---------------------------------------------------------------- #

$failures = New-Object Collections.Generic.List[string]

# The identity is resolved to a SID here, before anything is written, because
# it is the one input whose being wrong produces a working installation that
# the intended person cannot use - the tasks exist, the shortcuts exist, and
# every click says access denied.
$desktopSid = $null
try {
    $account = New-Object Security.Principal.NTAccount($DesktopUser)
    $desktopSid = $account.Translate([Security.Principal.SecurityIdentifier]).Value
    Write-Host "Desktop user '$DesktopUser' resolves to $desktopSid."
}
catch {
    $failures.Add("Could not resolve -DesktopUser '$DesktopUser' to an account on this machine. Use DOMAIN\user, .\user, or a local account name. ($($_.Exception.Message))")
}

if (-not (Get-VM -Name $VMName -ErrorAction SilentlyContinue)) {
    $failures.Add("No VM named '$VMName' on $env:COMPUTERNAME. This control stops and starts that VM, so it is installed on the host that carries it.")
}
else {
    $startAction = (Get-VM -Name $VMName).AutomaticStartAction
    if ("$startAction" -ne 'Nothing') {
        # Not fatal. The control works either way; what breaks is finding 6 -
        # a Windows Update reboot mid-evening brings the node back with nobody
        # having asked. Loud, because it is invisible until the night it isn't.
        Write-Warning "VM '$VMName' has AutomaticStartAction = $startAction, not Nothing. Hyper-V will start this node at the next host boot regardless of the state file, which undoes personal mode across a Windows Update reboot - and Set-UpdateRebootSchedule.ps1 sets NoAutoRebootWithLoggedOnUsers = 0 deliberately, so that reboot goes straight through an active session. Re-dispatch Provision 0 with automatic_start_action: Nothing, or run: Set-VM -Name $VMName -AutomaticStartAction Nothing"
    }
}

$runnerServices = @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue)
if ($runnerServices.Count -eq 0) {
    Write-Warning "No actions.runner.* service on this host. Personal mode will still drain the node and stop the VM; it just has no runner to quiesce. Register the runner (plan step 3.3) and re-run this to have it covered."
}
else {
    Write-Host "Runner service(s): $(($runnerServices | ForEach-Object { $_.Name }) -join ', ')"
}

if (-not (Test-Path $DesktopPath -PathType Container)) {
    $failures.Add("Desktop path '$DesktopPath' does not exist. Pass -DesktopPath explicitly.")
}

$sourceControl = Join-Path $PSScriptRoot 'Set-PersonalMode.ps1'
$sourceSsh = Join-Path $PSScriptRoot 'lib\AerieSsh.ps1'
$sourceWatch = Join-Path $PSScriptRoot 'lib\Watch-PersonalModeTask.ps1'
foreach ($src in $sourceControl, $sourceSsh, $sourceWatch) {
    if (-not (Test-Path $src -PathType Leaf)) { $failures.Add("Missing '$src' - run this from a complete checkout of scripts/hyperv.") }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    throw "Preflight failed with $($failures.Count) problem(s). Nothing was installed."
}

if ($WhatIfOnly) {
    Write-Host ''
    Write-Host 'WhatIfOnly - this run would have installed:' -ForegroundColor Yellow
    Write-Host "  $InstallPath\  (bin\, config.json, node.key, personal-mode.log)"
    Write-Host "  Tasks: $TaskEnter, $TaskExit, $TaskBoot, $TaskAutoExit (daily at ${AutoExitHour}:00)"
    Write-Host "  Run rights on the first two for $DesktopUser ($desktopSid)"
    Write-Host "  Shortcuts in $DesktopPath"
    Write-Host ''
    Write-Host 'state.json is never written by this script, so nobody mid-evening loses their machine to a re-registration.'
    return
}

# ---------------------------------------------------------------- #
Write-Stage 'Install'
# ---------------------------------------------------------------- #

New-Item -ItemType Directory -Path $libPath -Force | Out-Null
Copy-Item -Path $sourceControl -Destination $controlScript -Force
Copy-Item -Path $sourceSsh -Destination (Join-Path $libPath 'AerieSsh.ps1') -Force
Copy-Item -Path $sourceWatch -Destination $watchScript -Force
Write-Host "Copied the control and its library into $binPath."

# The key. Written with LF endings and a trailing newline for the same reason
# Resolve-SshPrivateKeyFile does it: OpenSSH rejects a key file with CRLF, and
# equally rejects one whose PEM footer has no trailing newline.
$keyContent = if ($PSCmdlet.ParameterSetName -eq 'KeyPath') { Get-Content $SshPrivateKeyPath -Raw } else { $SshPrivateKey }
[IO.File]::WriteAllText($keyPath, ($keyContent.Replace("`r`n", "`n").TrimEnd() + "`n"))

# SYSTEM and Administrators, nothing else, inheritance off. The desktop user
# is deliberately not on this list: the entire design is that they trigger a
# task and SYSTEM does the privileged part, so a key they could read would be
# privilege this arrangement went out of its way not to give them.
#
# Windows OpenSSH refuses a key file other principals can read - "UNPROTECTED
# PRIVATE KEY FILE" - and a file written under ProgramData inherits an ACL
# that grants Users read, which trips exactly that check. So inheritance is
# stripped and two ACEs are granted, the same shape as
# lib/AerieSsh.ps1's Protect-PrivateKeyFile but for SYSTEM rather than for
# whoever ran this: the tasks are what use the key, and they run as SYSTEM.
$acl = Get-Acl -Path $keyPath
$acl.SetAccessRuleProtection($true, $false)   # protect from inheritance, don't copy inherited ACEs
foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRule($rule) | Out-Null }
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {   # SYSTEM, BUILTIN\Administrators
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'Allow')))
}
Set-Acl -Path $keyPath -AclObject $acl
Write-Host "Installed the node key at $keyPath, readable by SYSTEM and Administrators only."

# ssh(1) refuses a key whose known_hosts sibling it cannot write, and the
# tasks run as SYSTEM with no home directory worth speaking of, so the file is
# created here rather than discovered missing on a Friday night.
$knownHosts = Join-Path $InstallPath 'known_hosts'
if (-not (Test-Path $knownHosts)) { New-Item -ItemType File -Path $knownHosts -Force | Out-Null }

# The log is created now, and left world-readable by ProgramData's own
# inherited ACL, because the shortcuts tail it from the desktop user's
# unprivileged session. Nothing sensitive is written to it - it carries step
# names, results and elapsed seconds.
if (-not (Test-Path $logPath)) { New-Item -ItemType File -Path $logPath -Force | Out-Null }

[ordered]@{
    vmName            = $VMName
    nodeName          = $NodeName
    serverAddress     = $ServerAddress
    nodeAddress       = $NodeAddress
    username          = $Username
    sshPrivateKeyPath = $keyPath
    registeredUtc     = (Get-Date).ToUniversalTime().ToString('o')
    registeredBy      = "$env:USERDOMAIN\$env:USERNAME"
} | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
Write-Host "Wrote $configPath."

# ---------------------------------------------------------------- #
Write-Stage 'Tasks'
# ---------------------------------------------------------------- #

function Register-PersonalModeTask {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Action,
        $Trigger,
        [string]$Description
    )

    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue

    $argument = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$controlScript`" $Action -ConfigPath `"$configPath`""
    $taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument

    # ExecutionTimeLimit at zero - unlimited - for the same reason
    # Register-VmConsoleLogShipper.ps1 sets it: the default is three days and
    # Task Scheduler kills the action at it without saying so. Three days is
    # not a risk for an action measured in seconds, but the setting is also
    # what stops a hung Enter from being *silently* killed halfway, which is
    # the state this control most needs never to be in. Its own deadlines are
    # what bound it; Task Scheduler is not a second, invisible opinion.
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    $params = @{
        TaskName = $Name
        Action   = $taskAction
        Settings = $settings
        User     = 'SYSTEM'
        RunLevel = 'Highest'
        Force    = $true
    }
    if ($Trigger) { $params.Trigger = $Trigger }
    if ($Description) { $params.Description = $Description }

    Register-ScheduledTask @params | Out-Null
    Write-Host "  registered $Name"
}

Register-PersonalModeTask -Name $TaskEnter -Action '-Enter' `
    -Description 'Hands this machine back to the person sitting at it: drains the node, stops the runner, stops the VM. No step can block the sequence. docs/plans/part-time-node.md 4.1'

Register-PersonalModeTask -Name $TaskExit -Action '-Exit' `
    -Description 'Returns this machine to the cluster: starts the runner and the VM, waits for Ready, uncordons, removes the personal-mode label. docs/plans/part-time-node.md 4.2'

Register-PersonalModeTask -Name $TaskBoot -Action '-Reconcile' `
    -Trigger (New-ScheduledTaskTrigger -AtStartup) `
    -Description 'At host boot, reads the personal-mode state file and makes the machine match it. The VM does not autostart (AutomaticStartAction Nothing) precisely so that this decides instead. docs/plans/part-time-node.md 4.5'

Register-PersonalModeTask -Name $TaskAutoExit -Action '-AutoExit' `
    -Trigger (New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.AddHours($AutoExitHour))) `
    -Description "Daily at ${AutoExitHour}:00 - gives the node back unless the pin is set. Forgetting should cost one night, not one month. docs/plans/part-time-node.md 4.6"

# ---------------------------------------------------------------- #
Write-Stage 'Rights'
# ---------------------------------------------------------------- #

# The whole privilege boundary, in one security descriptor per task.
#
# Replaced wholesale rather than appended to. Appending means parsing an SDDL
# whose default content varies by Windows build and by how the task was
# created, and getting that wrong on a task that runs as SYSTEM at highest
# privilege is the kind of mistake worth designing out. What these two tasks
# need is completely known: SYSTEM and Administrators may do anything with
# them, and one named user may read and run them. Nothing is lost by saying so
# outright.
#
#   D:P            a DACL, protected - no inherited ACEs
#   (A;;FA;;;SY)   SYSTEM, full
#   (A;;FA;;;BA)   BUILTIN\Administrators, full
#   (A;;GRGX;;;S)  the desktop user: generic read + generic execute. Execute
#                  on a task is the right to run it. Not write - they cannot
#                  change what it does or who it runs as, which is the point.
$sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$desktopSid)"

$scheduleService = New-Object -ComObject 'Schedule.Service'
$scheduleService.Connect()
$rootFolder = $scheduleService.GetFolder('\')
foreach ($name in $TaskEnter, $TaskExit) {
    $registered = $rootFolder.GetTask($name)
    # 4 = DACL_SECURITY_INFORMATION. 0 = no flags.
    $registered.SetSecurityDescriptor($sddl, 0)
    Write-Host "  $DesktopUser may run $name"
}

# The other two are triggered by Windows, never by a person, so they keep the
# default descriptor. A user who could run the boot task by hand could put the
# node back mid-evening, which is a strange thing to hand someone whose whole
# problem is wanting the machine to themselves.
Write-Host "  $TaskBoot and $TaskAutoExit stay administrator-only - nobody triggers them by hand"

# ---------------------------------------------------------------- #
Write-Stage 'Shortcuts'
# ---------------------------------------------------------------- #

# Not a direct `schtasks /run`: that returns the instant the task launches,
# and the task's own output goes to session 0 where nobody can see it. The
# watcher runs in the user's session, starts the task and tails the log, which
# is what turns ninety seconds of silence into ninety seconds of steps.
function New-PersonalModeShortcut {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][int]$IconIndex
    )
    $linkPath = Join-Path $DesktopPath $FileName
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$watchScript`" -TaskName $TaskName -LogPath `"$logPath`""
    $shortcut.WorkingDirectory = $binPath
    $shortcut.Description = $Description
    $shortcut.IconLocation = "$env:SystemRoot\System32\shell32.dll,$IconIndex"
    $shortcut.Save()
    Write-Host "  $linkPath"
}

New-PersonalModeShortcut -FileName 'Aerie - This machine is mine.lnk' -TaskName $TaskEnter -IconIndex 27 `
    -Description 'Take this machine out of the cluster for the evening. About a minute.'
New-PersonalModeShortcut -FileName 'Aerie - Rejoin the cluster.lnk' -TaskName $TaskExit -IconIndex 46 `
    -Description 'Give this machine back to the cluster.'

# ---------------------------------------------------------------- #
Write-Stage 'Done'
# ---------------------------------------------------------------- #

Write-Host ''
Write-Host "Personal-mode control installed on $env:COMPUTERNAME." -ForegroundColor Green
Write-Host ''
Write-Host 'Read the current state at any time, from any shell:'
Write-Host "  & '$controlScript' -Status"
Write-Host ''
Write-Host 'Rehearse both directions now, before anyone relies on them:'
Write-Host "  & '$controlScript' -Enter"
Write-Host "  & '$controlScript' -Exit"
Write-Host ''
Write-Host 'Then have the desktop user click both shortcuts, which is the path that'
Write-Host 'actually has to work - running the script as an administrator proves the'
Write-Host 'sequence, not the privilege boundary underneath it.'
Write-Host ''
Write-Host 'Still to do by hand, and named here rather than only in the plan:'
Write-Host '  - 4.7: pull the power mid-session. The house should stay up, the node should'
Write-Host '    go NotReady, its pods should reschedule, and the boot task should bring it'
Write-Host '    back correctly on the next power-on. That is the path that will happen.'
