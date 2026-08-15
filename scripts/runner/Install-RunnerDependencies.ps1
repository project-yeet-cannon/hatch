<#
.SYNOPSIS
    Installs the third-party tooling the Aerie provisioning scripts expect to
    find on a self-hosted runner: the AWS CLI v2 and the Windows OpenSSH
    client.

.DESCRIPTION
    Every provisioning workflow runs this as its first step after checkout, so
    a runner converges on the tools it needs by being used rather than by
    somebody remembering to prepare it. That is the same infrastructure-as-code
    rule the rest of scripts\ follows (docs/ethos.md): a prerequisite written
    only in a README is a prerequisite that will be wrong on the next machine.

    Idempotent and cheap when there's nothing to do - present-and-current tools
    are resolved and reported, not reinstalled - so running it on every
    dispatch costs a second or two.

    What it does *not* do: install the runner service itself, or anything the
    OS ships enabled. Registering a runner is the one genuinely manual step,
    because it needs a registration token that only a human can mint.

    Versions come from scripts\versions.json, not from parameters here, for the
    reason that file documents: a pin is structural, identical for every
    installation, so it belongs in git.

.PARAMETER Dependency
    Which tools to ensure. Defaults to all of them; each workflow names only
    what its script actually uses, so a k3s install doesn't fail on a machine
    with no reason to hold AWS credentials.

.PARAMETER CheckOnly
    Report what's missing and fail without touching the machine. What to use
    to audit a runner from a non-elevated session.

.EXAMPLE
    # What Provision 2 runs
    .\Install-RunnerDependencies.ps1 -Dependency AwsCli, OpenSshClient

.EXAMPLE
    # Audit this machine without changing it
    .\Install-RunnerDependencies.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('AwsCli', 'OpenSshClient')]
    [string[]]$Dependency = @('AwsCli', 'OpenSshClient'),

    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\AerieRunnerDependencies.ps1')

Write-Host "Ensuring runner dependencies: $($Dependency -join ', ')$(if ($CheckOnly) { ' (-CheckOnly)' })"
Write-Host ''

# Collected rather than thrown one at a time, for the same reason the
# provisioning scripts batch their preflight failures: one run should report
# everything wrong with the machine, not send the operator round the loop once
# per missing tool.
$failures = New-Object Collections.Generic.List[string]

foreach ($name in ($Dependency | Select-Object -Unique)) {
    try {
        switch ($name) {
            'AwsCli' { Install-AerieAwsCli -CheckOnly:$CheckOnly | Out-Null }
            'OpenSshClient' { Install-AerieOpenSshClient -CheckOnly:$CheckOnly }
        }
    }
    catch {
        $failures.Add("$name`: $($_.Exception.Message)")
    }
}

if ($failures.Count -gt 0) {
    $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
    throw "$($failures.Count) runner dependency problem(s):`n$detail"
}

Write-Host ''
Write-Host 'Runner dependencies OK.' -ForegroundColor Green
