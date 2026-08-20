<#
.SYNOPSIS
    Installs the third-party tooling the Aerie workflows expect to find on a
    self-hosted runner - the AWS CLI v2 and the Windows OpenSSH client for the
    provisioning scripts, and the build toolchain (pwsh, kubectl, helm, jq,
    gh, the Android SDK) that ci.yml and publish.yml used to get free from
    GitHub's ubuntu-latest image.

.DESCRIPTION
    Every provisioning workflow runs this as its first step after checkout, so
    a runner converges on the tools it needs by being used rather than by
    somebody remembering to prepare it. That is the same infrastructure-as-code
    rule the rest of scripts\ follows (docs/ethos.md): a prerequisite written
    only in a README is a prerequisite that will be wrong on the next machine.

    Idempotent and cheap when there's nothing to do - present-and-current tools
    are resolved and reported, not reinstalled - so running it on every
    dispatch costs a second or two.

    What it does *not* do: install the runner service itself, anything the OS
    ships enabled, or Docker. Registering a runner needs a registration token
    that only a human can mint. Docker is excluded on different grounds -
    building this repository's Linux images on a Windows host means Docker
    Desktop or a VM, not an unattended MSI - so 'Docker' here is a check that
    reports precisely what is wrong rather than an install.

    Versions come from scripts\versions.json, not from parameters here, for the
    reason that file documents: a pin is structural, identical for every
    installation, so it belongs in git.

.PARAMETER Dependency
    Which tools to ensure. Each workflow names only what its own jobs
    actually use, so a k3s install doesn't fail on a machine with no reason to
    hold AWS credentials, and a container build doesn't drag in a 700MB
    Android SDK.

    The default is everything *except* AndroidSdk - that one is large enough,
    and needed by few enough jobs, that it should be asked for by name. Note
    it also has an ordering requirement the others don't: sdkmanager is a Java
    program, so actions/setup-java has to run before it.

.PARAMETER CheckOnly
    Report what's missing and fail without touching the machine. What to use
    to audit a runner from a non-elevated session.

.EXAMPLE
    # What Provision 2 runs
    .\Install-RunnerDependencies.ps1 -Dependency AwsCli, OpenSshClient

.EXAMPLE
    # What ci.yml's deploy-manifests job runs
    .\Install-RunnerDependencies.ps1 -Dependency PowerShell7, Kubectl, Helm, Jq

.EXAMPLE
    # Audit this machine without changing it
    .\Install-RunnerDependencies.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('AwsCli', 'OpenSshClient', 'GitBash', 'PowerShell7', 'Kubectl', 'Helm', 'Jq', 'GitHubCli', 'Docker', 'AndroidSdk')]
    [string[]]$Dependency = @('AwsCli', 'OpenSshClient', 'GitBash', 'PowerShell7', 'Kubectl', 'Helm', 'Jq', 'GitHubCli', 'Docker'),

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
            'GitBash' { Install-AerieGitBash -CheckOnly:$CheckOnly | Out-Null }
            'PowerShell7' { Install-AeriePowerShell7 -CheckOnly:$CheckOnly | Out-Null }
            'Kubectl' { Install-AerieKubectl -CheckOnly:$CheckOnly | Out-Null }
            'Helm' { Install-AerieHelm -CheckOnly:$CheckOnly | Out-Null }
            'Jq' { Install-AerieJq -CheckOnly:$CheckOnly | Out-Null }
            'GitHubCli' { Install-AerieGitHubCli -CheckOnly:$CheckOnly | Out-Null }
            'Docker' { Install-AerieDocker -CheckOnly:$CheckOnly }
            'AndroidSdk' { Install-AerieAndroidSdk -CheckOnly:$CheckOnly | Out-Null }
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
