<#
.SYNOPSIS
    Registers (or re-registers) the Scheduled Task that ships one VM's serial
    console to OpenSearch via Send-VmConsoleLog.ps1.

.DESCRIPTION
    A Scheduled Task, not a background job spawned by New-AerieVM.ps1 itself,
    because this needs to keep running for the VM's lifetime - months, across
    host reboots - while anything spawned as a child of a GitHub Actions step
    gets killed by the runner's job-object cleanup the moment that step ends.

    Runs as SYSTEM: this host is already trusted at Administrator level to
    run Hyper-V cmdlets (see Initialize-AerieNode.ps1's #Requires), and the
    named pipe Hyper-V exposes for a VM's COM port doesn't need anything
    higher.

    Idempotent - unregisters any existing task of the same name first, so
    calling this again (e.g. after -RecreateVM) replaces rather than
    duplicates it.
#>
function Register-VmConsoleLogShipper {
    param(
        [Parameter(Mandatory)][string]$VMName,
        [Parameter(Mandatory)][string]$VmDir,
        [Parameter(Mandatory)][string]$IngestUrl,
        [Parameter(Mandatory)][string]$Token
    )

    $shipperScript = Join-Path $VmDir 'console-log-shipper.ps1'
    Copy-Item -Path (Join-Path $PSScriptRoot 'Send-VmConsoleLog.ps1') -Destination $shipperScript -Force

    $taskName = "Aerie-VMConsoleLog-$VMName"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

    $pipeName = "$VMName-com1"
    # Alongside the VM's disks, so the console log is found by anyone already
    # looking at that VM's directory and is removed with it on -RecreateVM.
    $consoleLogPath = Join-Path $VmDir 'console.log'
    $argumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$shipperScript`" " +
        "-VMName `"$VMName`" -PipeName `"$pipeName`" -IngestUrl `"$IngestUrl`" -Token `"$Token`" " +
        "-LogFilePath `"$consoleLogPath`""
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argumentList

    # AtStartup covers the host-reboot case; Register-VmConsoleLogShipper's
    # caller also does Start-ScheduledTask immediately below to cover today,
    # since the VM (and its pipe) exists now, not at the next boot.
    $trigger = New-ScheduledTaskTrigger -AtStartup

    # ExecutionTimeLimit defaults to 3 days (PT72H) - without overriding it
    # to unlimited, Task Scheduler silently kills this after 72 hours.
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
}
