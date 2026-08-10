<#
.SYNOPSIS
    Idempotently sets this host's Windows Update install/reboot schedule
    (TODO_SWARM.md Phase 1: stagger reboots across hosts).

.DESCRIPTION
    Configures the "Configure Automatic Updates" AU policy under
    HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU so Windows
    installs updates and reboots on one fixed day/hour per week. Native
    mechanism, no custom Scheduled Task - Windows Server honors this policy
    without WSUS, and Windows itself owns the install + reboot from here.

    The day/hour are decided by the caller (.github/workflows/
    stagger-update-reboots.yml), which spreads the current set of hosts
    across the week so no two land on the same day - the etcd quorum this
    protects tolerates exactly one node down at a time.

    NoAutoRebootWithLoggedOnUsers is deliberately left at 0 (reboot even
    through an active admin session): a reboot silently skipped because
    someone left a session open would break the stagger guarantee without
    anyone noticing.
#>
#Requires -RunAsAdministrator
param(
    # Windows' own AU encoding: 1=Sunday .. 7=Saturday.
    [Parameter(Mandatory)][ValidateRange(1, 7)][int]$ScheduledInstallDay,
    [Parameter(Mandatory)][ValidateRange(0, 23)][int]$ScheduledInstallHour,
    [ValidateRange(0, 64)][int]$RebootGraceMinutes = 15
)

$ErrorActionPreference = 'Stop'

$dayNames = @('', 'Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')

$auPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
New-Item -Path $auPath -Force | Out-Null

$values = [ordered]@{
    NoAutoUpdate                           = 0
    AUOptions                              = 4  # auto download, notify for scheduled install
    ScheduledInstallDay                    = $ScheduledInstallDay
    ScheduledInstallTime                   = $ScheduledInstallHour
    AlwaysAutoRebootAtScheduledTime        = 1
    AlwaysAutoRebootAtScheduledTimeMinutes = $RebootGraceMinutes
    NoAutoRebootWithLoggedOnUsers          = 0
}
foreach ($name in $values.Keys) {
    Set-ItemProperty -Path $auPath -Name $name -Value $values[$name] -Type DWord
}

# So the change takes effect now instead of waiting for the Update Agent's
# own refresh cycle to notice the policy changed.
Restart-Service -Name wuauserv -Force

Write-Host "Windows Update on $env:COMPUTERNAME: install + reboot every $($dayNames[$ScheduledInstallDay]) at ${ScheduledInstallHour}:00 (+${RebootGraceMinutes}m grace)." -ForegroundColor Green
Get-ItemProperty -Path $auPath | Select-Object -Property ([string[]]$values.Keys) | Format-List | Out-Host
