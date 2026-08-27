<#
.SYNOPSIS
    Raises this runner's PowerShell execution policy far enough that a
    `shell: powershell` step can run at all. The one prerequisite that cannot
    install itself from a PowerShell step, because a PowerShell step is what it
    unblocks.

.DESCRIPTION
    GitHub Actions implements `shell: powershell` by writing the step's body to
    a temp `.ps1` and dot-sourcing it:

        powershell.EXE -command ". 'C:\actions-runner\_work\_temp\<guid>.ps1'"

    Under an execution policy of `Restricted` - which is the **Windows client
    default** - that dot-source is refused before a single line of the step
    runs:

        File ...ps1 cannot be loaded because running scripts is disabled on
        this system.

    Which is why `Set-ExecutionPolicy -Scope Process` at the top of every step
    in this repository does not help: it is inside the file that will not load.

    **Windows Server defaults to `RemoteSigned`, so this never appeared on the
    first three hosts.** It appears the moment an installation adds a host that
    is somebody's desktop - which is exactly what the part-time node is
    (docs/plans/part-time-node.md). A prerequisite that only bites on the
    fourth machine is a prerequisite that will be diagnosed from scratch on the
    fourth machine, so it is converged here instead of documented.

    ## CurrentUser, not LocalMachine, and that is deliberate

    The scope raised is `CurrentUser` - the account the runner service runs as.
    It sits above `LocalMachine` in PowerShell's precedence chain
    (MachinePolicy > UserPolicy > Process > CurrentUser > LocalMachine), so it
    is sufficient on a machine whose LocalMachine policy is `Restricted`, and
    it changes nothing about how PowerShell behaves for the person who logs
    into that desktop.

    That distinction matters most on the one host where it is most likely to be
    needed. A machine that belongs to a person should not have its owner's
    shell quietly reconfigured because a build agent lives on it.

    ## RemoteSigned, not Bypass

    `RemoteSigned` refuses unsigned scripts that carry a mark of the web, and
    permits local ones. Files written by `actions/checkout` and by the runner's
    own temp writer carry no such mark, so it is sufficient - which is not a
    guess: the three Windows Server hosts have run this repository under
    `RemoteSigned` since the beginning. `Bypass` would also work and would be a
    larger permission than the evidence calls for.

    ## When it cannot help, it says so

    `MachinePolicy` and `UserPolicy` come from Group Policy and outrank
    everything settable here, including the `-ExecutionPolicy Bypass` flag on a
    command line. If either is restrictive this stops with that named as the
    cause, rather than setting `CurrentUser` successfully - which PowerShell
    reports as a *warning*, not an error - and leaving the next step to fail
    with the original confusing message.

.PARAMETER Scope
    Which scope to write. `CurrentUser` is the default and the argument above.
    `LocalMachine` is for an operator who would rather the whole machine agree,
    and needs an elevated session.

.PARAMETER Policy
    What to raise it to. `RemoteSigned` is the default and the argument above.

.EXAMPLE
    # What the composite action runs, and what to run by hand on a host that
    # has not been through a workflow yet.
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\runner\Set-RunnerExecutionPolicy.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$Scope = 'CurrentUser',

    [ValidateSet('RemoteSigned', 'Unrestricted', 'Bypass')]
    [string]$Policy = 'RemoteSigned'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The policies under which the runner can dot-source its own temp script.
# AllSigned is absent on purpose: nothing this repository writes is signed, so
# a host set to AllSigned is a host this cannot fix by raising a scope.
$Sufficient = @('RemoteSigned', 'Unrestricted', 'Bypass')

function Get-ScopePolicy {
    param([Parameter(Mandatory)][string]$Name)
    $entry = Get-ExecutionPolicy -List | Where-Object { "$($_.Scope)" -eq $Name }
    if (-not $entry) { return 'Undefined' }
    return "$($entry.ExecutionPolicy)"
}

Write-Host 'Execution policy by scope, before:'
Get-ExecutionPolicy -List | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }

$effective = "$(Get-ExecutionPolicy)"
if ($Sufficient -contains $effective) {
    Write-Host "Effective policy is $effective - a 'shell: powershell' step can run here. Nothing to change." -ForegroundColor Green
    return
}

Write-Host "Effective policy is $effective, which refuses to load the temp .ps1 every 'shell: powershell' step is written to." -ForegroundColor Yellow

# Group Policy outranks every scope this script can write, and it outranks the
# -ExecutionPolicy flag on a command line too. Checked before writing anything,
# so the failure names its cause instead of being a successful write with no
# effect.
foreach ($gpScope in 'MachinePolicy', 'UserPolicy') {
    $gpPolicy = Get-ScopePolicy -Name $gpScope
    if ($gpPolicy -ne 'Undefined' -and $Sufficient -notcontains $gpPolicy) {
        throw @"
$gpScope is set to '$gpPolicy' by Group Policy, which outranks every scope this script can write - and outranks the -ExecutionPolicy flag on a command line as well. Nothing here can raise the effective policy above it.

This runner cannot run 'shell: powershell' steps until that policy is changed or this machine is excluded from it. The setting is Computer (or User) Configuration > Administrative Templates > Windows Components > Windows PowerShell > Turn on Script Execution.
"@
    }
}

Write-Host "Setting $Scope to $Policy."
Set-ExecutionPolicy -Scope $Scope -ExecutionPolicy $Policy -Force

# Read back rather than trust. Set-ExecutionPolicy reports a scope it could not
# make effective as a *warning* on success, which is exactly the shape of
# failure that would otherwise be discovered as the next step failing with the
# message this script exists to prevent.
$effective = "$(Get-ExecutionPolicy)"
Write-Host ''
Write-Host 'Execution policy by scope, after:'
Get-ExecutionPolicy -List | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }

if ($Sufficient -notcontains $effective) {
    throw "Set $Scope to $Policy, but the effective policy is still '$effective' - something at a higher-precedence scope is overriding it. The table above says which."
}

Write-Host "Effective policy is now $effective." -ForegroundColor Green
