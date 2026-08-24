<#
.SYNOPSIS
    Asserts every *Exit* criterion of the cluster plan Phase 8b in one run,
    the same shape scripts/k3s/Test-ClusterPlatform.ps1 established for Phase
    3b.13 and Test-Cutover.ps1 continued for 7c.11. "Phase 8 is done" as a
    command rather than as a memory.

.DESCRIPTION
    Phase 8b.16. Three backup paths, one alert family, and the handful of
    facts that make each of them true rather than configured.

    Same three properties every gate before it states and relies on:

    **It does not stop at the first failure.** A table of sixteen findings is
    worth more than the first one, and nothing here changes the cluster.

    **A check it cannot evaluate is a failure, not a skip.** An absent object,
    an unparseable response or a probe that did not run is "not proven", and a
    gate that reports that as anything but failure can be satisfied by a
    cluster that is switched off.

    **Expectations come from the cluster and the repository, not from
    parameters.** The expected Secret key sets come from
    scripts/secrets/parameters.json; the expected alert names and thresholds
    come from the committed deploy/cluster/observability/config/alerts/backup.yaml;
    the backup target URL is rebuilt from the live aerie-cluster-config
    ConfigMap. Nothing here is a number to remember to pass.

    **The one exception to read-only, and it is deliberate.** Two of this
    phase's exit criteria - that `cutover-final` is still in both restic
    repositories, and that the newest snapshot in each holds all three files -
    cannot be answered by reading Kubernetes objects. They need `restic` run
    against both repositories with the repository password, and nothing
    long-lived in this cluster has both. So the probe stage creates one
    short-lived Job in the `aerie` namespace, **built from the live
    aerie-backup CronJob's own pod template** so it inherits that job's image,
    credentials and mounts rather than restating them, replaces its command
    with three read-only restic calls, reads its log and deletes it. It runs
    `snapshots`, `ls` and `dump`: no `backup`, no `forget`, no `prune`, and
    nothing that takes an exclusive lock. The alternative was to report two of
    the phase's most important facts as unprovable, which is worse.

    Stages:
      1. Preflight   - the SSH key resolves, the client is present, the node
                       answers 22, and the committed maps and manifests parse.
      2. Probe       - one round trip collects every Kubernetes object and the
                       Prometheus rules the checks below reason about.
      3. Restic      - the one Job above, created, read and deleted.
      4. Checks      - 8a.3 and 8b.1-8b.13, evaluated against that snapshot.
      5. Portability - this phase's own per-installation values, against deploy/.
      6. Report      - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here is cluster
    state.

.PARAMETER SkipResticProbe
    Skip stage 3 and report its two checks as failures rather than running a
    Job. For a run that must not create an object at all - and the honest cost
    of that is a gate that cannot pass.

.EXAMPLE
    .\Test-Backup.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$MapPath = (Join-Path $PSScriptRoot 'cluster-config.json'),
    [string]$ParametersPath = (Join-Path $PSScriptRoot '..\secrets\parameters.json'),
    [string]$DeployPath = (Join-Path $PSScriptRoot '..\..\deploy'),
    [string]$AlertsManifestPath = (Join-Path $PSScriptRoot '..\..\deploy\cluster\observability\config\alerts\backup.yaml'),

    [string]$Username = 'aerie',

    [switch]$SkipResticProbe,

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# The staleness thresholds, mirroring
# deploy/cluster/observability/config/alerts/backup.yaml. They are constants
# here rather than parsed out of that file - a regex over a PromQL expression
# is a worse dependency than a number with a check beside it - and
# 'Alert thresholds match this gate' below fails if the file moves without
# this script following.
$BackupMaxAgeHours = 36
$VerifyMaxAgeDays = 10

# 8b.5's staging path, which is fixed precisely so that a name can be asserted
# (and so that restic's retention grouping works - finding 9).
$ExpectedSnapshotFiles = @('aerie.dump', 'quartz.dump', 'parameters.json')

# 8a.3's label, and the class whose PVCs define the set it must equal.
$CriticalVolumeLabel = 'recurring-job-group.longhorn.io/aerie-critical'
$CriticalStorageClass = 'longhorn-r3'

$GateJobName = 'aerie-backup-gate-probe'

$script:StageNumber = 0
function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Get-Field {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Path {
    <#
    .SYNOPSIS
        Walks a dotted path of property names, returning $null the moment any
        link is missing - an absent path and an absent value both mean "not
        proven" to a gate.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Path)
    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        $current = Get-Field $current $segment
    }
    return $current
}

function Get-Items {
    param([Parameter(Mandatory)][AllowNull()]$List)
    return @(Get-Field $List 'items' | Where-Object { $_ })
}

function Get-Condition {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Type)
    $conditions = @(Get-Path $Object 'status.conditions' | Where-Object { $_ })
    return ($conditions | Where-Object { (Get-Field $_ 'type') -eq $Type } | Select-Object -First 1)
}

function Format-Condition {
    param([Parameter(Mandatory)][AllowNull()]$Condition, [int]$MaxLength = 160)
    if ($null -eq $Condition) { return 'no Ready condition' }
    $reason = [string](Get-Field $Condition 'reason')
    $message = [string](Get-Field $Condition 'message') -replace '\s+', ' '
    $text = (@($reason, $message) | Where-Object { $_ }) -join ': '
    if (-not $text) { $text = [string](Get-Field $Condition 'status') }
    if ($text.Length -gt $MaxLength) { $text = $text.Substring(0, $MaxLength - 1) + [char]0x2026 }
    return $text
}

function Get-ProbeSection {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Output, [Parameter(Mandatory)][string]$Name)
    $lines = @($Output -split "`r?`n")
    $collected = New-Object Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $lines) {
        if ($line.TrimEnd() -eq "--- $Name") { $inSection = $true; continue }
        if ($line -match '^--- \S+$') { if ($inSection) { break }; continue }
        if ($inSection) { $collected.Add($line) }
    }
    return ($collected -join "`n").Trim()
}

function ConvertFrom-ProbeJson {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Output, [Parameter(Mandatory)][string]$Name)
    $section = Get-ProbeSection -Output $Output -Name $Name
    if ([string]::IsNullOrWhiteSpace($section)) { return $null }
    try { return $section | ConvertFrom-Json }
    catch { return $null }
}

$script:Checks = New-Object Collections.Generic.List[psobject]
function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Fail', 'Warn')][string]$Status,
        [string]$Detail = ''
    )
    $script:Checks.Add([pscustomobject]@{ Step = $Step; Check = $Name; Result = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'Pass' { 'DarkGray' } 'Warn' { 'Yellow' } default { 'Red' } }
    $marker = switch ($Status) { 'Pass' { 'ok  ' } 'Warn' { 'warn' } default { 'FAIL' } }
    Write-Host ("  [{0}] {1,-6} {2}{3}" -f $marker, $Step, $Name, $(if ($Detail) { " - $Detail" })) -ForegroundColor $colour
}

function ConvertTo-UtcDateTime {
    <#
    .SYNOPSIS
        One Kubernetes timestamp as a UTC [DateTime], whatever shape
        ConvertFrom-Json handed it back in - or $null if it is not a timestamp
        at all. See Test-DataTier.ps1's copy for the four-hour bug that made
        this a function rather than a cast.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Local) { return $Value.ToUniversalTime() }
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { return [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc) }
        return $Value
    }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    [DateTimeOffset]$parsed = [DateTimeOffset]::MinValue
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    if ([DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        return $parsed.UtcDateTime
    }
    return $null
}

function Add-CronJobAgeCheck {
    <#
    .SYNOPSIS
        One CronJob's newest successful run is younger than its own alert
        threshold - the manual form of 8b.11's staleness rules, and the thing
        that catches a rule which is silently absent().
    #>
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowNull()]$CronJob,
        [Parameter(Mandatory)][double]$MaxAgeHours,
        [Parameter(Mandatory)][DateTime]$NowUtc
    )
    if ($null -eq $CronJob) {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail 'CronJob not found - nothing is running this backup, and 8b.11 absent() sibling should be firing'
        return
    }
    if ((Get-Path $CronJob 'spec.suspend') -eq $true) {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail 'CronJob is suspended'
        return
    }
    $last = ConvertTo-UtcDateTime (Get-Path $CronJob 'status.lastSuccessfulTime')
    if ($null -eq $last) {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail 'no lastSuccessfulTime - it has never completed a run'
        return
    }
    $ageHours = [math]::Round(($NowUtc - $last).TotalHours, 1)
    if ($ageHours -le $MaxAgeHours) {
        Add-Check -Step $Step -Name $Name -Status 'Pass' -Detail "${ageHours}h old, threshold ${MaxAgeHours}h"
    }
    else {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail "${ageHours}h old, past its ${MaxAgeHours}h alert threshold"
    }
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    foreach ($path in @($MapPath, $ParametersPath, $AlertsManifestPath)) {
        if (-not (Test-Path $path -PathType Leaf)) { $failures.Add("Not found: '$path'. Run this from a checkout of the repository the cluster reconciles from.") }
    }
    if (-not (Test-Path $DeployPath -PathType Container)) { $failures.Add("Not found: '$DeployPath'.") }

    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
        $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey.')
    }
    if ($SshPrivateKeyPath -and -not $SshPrivateKey -and -not (Test-Path $SshPrivateKeyPath -PathType Leaf)) {
        $failures.Add("SSH private key not found at '$SshPrivateKeyPath'.")
    }

    $opensshOk = $true
    try { Assert-OpenSshClient } catch { $opensshOk = $false; $failures.Add($_.Exception.Message) }

    $privateKeyPath = $null
    $keyFingerprint = $null
    if ($opensshOk -and ($SshPrivateKey -or ($SshPrivateKeyPath -and (Test-Path $SshPrivateKeyPath -PathType Leaf)))) {
        try {
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-backup-gate'
            $privateKeyPath = $resolvedKey.Path
            $tempKeyFile = $resolvedKey.TempFile
            $keyFingerprint = $resolvedKey.Fingerprint
        }
        catch { $failures.Add($_.Exception.Message) }
    }

    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        $failures.Add("$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right.")
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    $parameters = Get-Content -Path $ParametersPath -Raw | ConvertFrom-Json

    # The alert names this gate expects Prometheus to have loaded, read off the
    # committed manifest rather than listed here - a rule added to that file
    # and never loaded is exactly the drift this check exists for.
    $expectedAlertNames = @(
        Select-String -Path $AlertsManifestPath -Pattern '^\s*-\s*alert:\s*(\S+)\s*$' |
            ForEach-Object { $_.Matches[0].Groups[1].Value }
    )
    $alertsManifestText = Get-Content -Path $AlertsManifestPath -Raw

    Write-Host "Cluster:  $IPAddress"
    Write-Host "Repo:     $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host "Alerts:   $($expectedAlertNames.Count) rule(s) declared in backup.yaml"
    Write-Host 'Preflight OK. Stage 2 reads only; stage 3 creates one Job that runs three read-only restic commands and deletes it.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-backup-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip, the same discipline every gate here uses: every command
    # falls back to `echo {}` on an unregistered type, so a half-built backup
    # tier answers "no items" rather than aborting the probe on its first
    # missing CRD. Single quotes only, no double quotes anywhere -
    # Invoke-NodeSsh refuses a command containing one, because Windows
    # PowerShell 5.1 lets ssh.exe strip it and run a subtly different script.
    #
    # Secret *keys* are read as byte counts through `wc -c` on the node, never
    # as `-o json`: this output is a CI run log, and a Secret's json is its
    # credential in base64.
    $probeScript = @(
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- esrestic\n'''
        'sudo k3s kubectl -n aerie get externalsecrets.external-secrets.io restic -o json 2>/dev/null || echo {}'
        'printf ''\n--- eslonghorn\n'''
        'sudo k3s kubectl -n longhorn-system get externalsecrets.external-secrets.io longhorn-backup-target -o json 2>/dev/null || echo {}'
        'printf ''\n--- resticsecretkeys\n'''
        'printf ''password ''; sudo k3s kubectl -n aerie get secret restic -o jsonpath={.data.password} 2>/dev/null | wc -c'
        'printf ''access-key-id ''; sudo k3s kubectl -n aerie get secret restic -o jsonpath={.data.access-key-id} 2>/dev/null | wc -c'
        'printf ''secret-access-key ''; sudo k3s kubectl -n aerie get secret restic -o jsonpath={.data.secret-access-key} 2>/dev/null | wc -c'
        'printf ''\n--- longhornsecretkeys\n'''
        'printf ''AWS_ACCESS_KEY_ID ''; sudo k3s kubectl -n longhorn-system get secret longhorn-backup-target -o jsonpath={.data.AWS_ACCESS_KEY_ID} 2>/dev/null | wc -c'
        'printf ''AWS_SECRET_ACCESS_KEY ''; sudo k3s kubectl -n longhorn-system get secret longhorn-backup-target -o jsonpath={.data.AWS_SECRET_ACCESS_KEY} 2>/dev/null | wc -c'
        'printf ''\n--- cronjobs\n'''
        'sudo k3s kubectl get cronjobs --all-namespaces -o json 2>/dev/null || echo {}'
        'printf ''\n--- backupcronjob\n'''
        'sudo k3s kubectl -n aerie get cronjob aerie-backup -o json 2>/dev/null || echo {}'
        'printf ''\n--- volumes\n'''
        'sudo k3s kubectl -n longhorn-system get volumes.longhorn.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- pvcs\n'''
        'sudo k3s kubectl get pvc --all-namespaces -o json 2>/dev/null || echo {}'
        'printf ''\n--- backuptarget\n'''
        'sudo k3s kubectl -n longhorn-system get backuptargets.longhorn.io default -o json 2>/dev/null || echo {}'
        'printf ''\n--- freezesetting\n'''
        'sudo k3s kubectl -n longhorn-system get settings.longhorn.io freeze-filesystem-for-snapshot -o json 2>/dev/null || echo {}'
        'printf ''\n--- restoresecret\n'''
        'sudo k3s kubectl -n aerie get secret aerie-pg-restore-restic -o name 2>/dev/null || echo none'
        'printf ''\n--- longhornbackups\n'''
        'sudo k3s kubectl -n longhorn-system get backups.longhorn.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- rules\n'''
        'sudo k3s kubectl get --raw ''/api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/rules?type=alert'' 2>/dev/null || echo {}'
        'printf ''\n--- end\n'''
    ) -join '; '

    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 30
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "The probe against $IPAddress failed as a whole$(if ($permanentReason) { ": $permanentReason" }). Nothing was checked:`n$($probe.StdErr)"
    }
    if ($probe.StdOut -notmatch '--- end') {
        throw "The probe against $IPAddress did not run to completion - its final marker is missing, so an unknown number of sections below are truncated rather than empty. Raw output:`n$($probe.StdOut)"
    }
    Write-Host "Collected $((@($probe.StdOut -split '--- ')).Count - 1) section(s) in one round trip."

    $clusterConfig = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'clusterconfig'
    $esRestic = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'esrestic'
    $esLonghorn = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'eslonghorn'
    $cronJobs = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'cronjobs'))
    $backupCronJob = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'backupcronjob'
    $volumes = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'volumes'))
    $pvcs = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pvcs'))
    $backupTarget = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'backuptarget'
    $freezeSetting = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'freezesetting'
    $longhornBackups = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'longhornbackups'))
    $rules = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'rules'
    $restoreSecretRaw = (Get-ProbeSection -Output $probe.StdOut -Name 'restoresecret').Trim()

    function Get-KeyBytes {
        param([Parameter(Mandatory)][string]$Section)
        return @((Get-ProbeSection -Output $probe.StdOut -Name $Section) -split "`n" | ForEach-Object {
                if ($_ -match '^(\S+)\s+(\d+)\s*$' -and [int]$Matches[2] -gt 0) { $Matches[1] }
            } | Where-Object { $_ })
    }
    $resticSecretKeys = Get-KeyBytes 'resticsecretkeys'
    $longhornSecretKeys = Get-KeyBytes 'longhornsecretkeys'

    $configData = Get-Field $clusterConfig 'data'

    # ---------------------------------------------------------------- #
    Write-Stage 'Restic'
    # ---------------------------------------------------------------- #

    # The one exception to read-only, argued in this file's .DESCRIPTION. The
    # Job is built from the live aerie-backup CronJob's own pod template, so
    # the image, the `restic` Secret env, the S3 repository substitution and
    # the SMB mount are whatever that CronJob actually runs with - a probe
    # that restated them could pass against a repository the nightly backup no
    # longer writes to, which is the exact failure this gate is for.
    $resticProbeOutput = $null
    $resticProbeError = $null

    if ($SkipResticProbe) {
        $resticProbeError = '-SkipResticProbe was passed'
    }
    elseif ($null -eq (Get-Path $backupCronJob 'spec.jobTemplate.spec.template')) {
        $resticProbeError = 'the aerie-backup CronJob has no pod template to build a probe from - it is missing or unreadable'
    }
    else {
        # restic only. `snapshots`, `ls` and `dump` read; none of them takes
        # the exclusive lock that `forget --prune` does, so this cannot
        # collide with the nightly job even if it runs at 03:10 exactly.
        $resticScript = @'
set -u
for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "--- repo $REPO"
  echo "--- cutover"
  restic -r "$REPO" snapshots --tag cutover-final --json | jq -c '[.[]? | {short_id, time}]' || echo ERROR
  echo "--- files"
  restic -r "$REPO" ls latest --json | jq -r 'select(.struct_type=="node") | .path' || echo ERROR
  echo "--- params"
  restic -r "$REPO" dump latest /tmp/aerie-backup/parameters.json | jq 'length' || echo ERROR
done
echo "--- resticend"
'@ -replace "`r`n", "`n"

        $template = Get-Path $backupCronJob 'spec.jobTemplate.spec.template'
        $container = @(Get-Path $template 'spec.containers')[0]
        $container.command = @('sh', '-c', $resticScript)
        if ($container.PSObject.Properties['args']) { $container.args = $null }
        # A gate probe should not retry a repository that answered: one
        # attempt, one log, one verdict.
        $jobManifest = [pscustomobject]@{
            apiVersion = 'batch/v1'
            kind       = 'Job'
            metadata   = [pscustomobject]@{ name = $GateJobName; namespace = 'aerie' }
            spec       = [pscustomobject]@{
                backoffLimit            = 0
                ttlSecondsAfterFinished = 300
                template                = $template
            }
        }
        $json = $jobManifest | ConvertTo-Json -Depth 40 -Compress

        # base64, not the raw manifest: Invoke-NodeSsh's -StdIn is a text
        # channel that has been observed welding a UTF-8 BOM onto what it
        # carries, and a BOM is fatal to JSON in a way it is not to YAML. That
        # is the pattern AerieSsh.ps1's own note prescribes.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))

        Write-Host "Creating Job aerie/$GateJobName from the live aerie-backup pod template..."
        $create = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -StdIn $encoded -Command (
            'sudo k3s kubectl -n aerie delete job ' + $GateJobName + ' --ignore-not-found >/dev/null 2>&1; ' +
            'base64 -d | sudo k3s kubectl -n aerie create -f - 2>&1'
        )
        if ($create.ExitCode -ne 0) {
            $resticProbeError = "could not create the probe Job: $($create.StdOut) $($create.StdErr)".Trim()
        }
        else {
            $wait = Invoke-NodeSsh @ssh -ConnectTimeoutSec 300 -Command (
                'sudo k3s kubectl -n aerie wait --for=condition=complete --timeout=240s job/' + $GateJobName + ' >/dev/null 2>&1; ' +
                'sudo k3s kubectl -n aerie logs job/' + $GateJobName + ' 2>&1'
            )
            $resticProbeOutput = $wait.StdOut
            if ($resticProbeOutput -notmatch '--- resticend') {
                $resticProbeError = "the probe Job did not run to completion; its log was: $($resticProbeOutput.Trim())"
                $resticProbeOutput = $null
            }
            else {
                Write-Host 'Probe Job completed.'
            }
        }

        $cleanup = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            'sudo k3s kubectl -n aerie delete job ' + $GateJobName + ' --ignore-not-found 2>&1'
        )
        if ($cleanup.ExitCode -ne 0) {
            Write-Host "Warning: could not delete Job aerie/$GateJobName - it has a 300s TTL and will go on its own." -ForegroundColor Yellow
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    $nowUtc = (Get-Date).ToUniversalTime()

    # -- 8b.1: the two ExternalSecrets, and their key sets from parameters.json
    foreach ($es in @(
            @{ Step = '8b.1'; Name = 'restic ExternalSecret'; Object = $esRestic; Secret = 'restic'; Namespace = 'aerie'; Keys = $resticSecretKeys },
            @{ Step = '8b.9'; Name = 'longhorn-backup-target ExternalSecret'; Object = $esLonghorn; Secret = 'longhorn-backup-target'; Namespace = 'longhorn-system'; Keys = $longhornSecretKeys }
        )) {
        # An absent object and an object with no Ready condition are both
        # failures, but they are different failures and the message says
        # which. The first time this gate ran, this check failed against an
        # ExternalSecret that `kubectl` reported as SecretSynced thirty
        # seconds later and has reported as SecretSynced on every run since -
        # so what the probe saw was either an empty `{}` from the `||`
        # fallback or an object mid-write, and 'no Ready condition' could not
        # tell those apart. It can now.
        $ready = Get-Condition $es.Object 'Ready'
        if ((Get-Field $ready 'status') -eq 'True' -and (Get-Field $ready 'reason') -eq 'SecretSynced') {
            Add-Check -Step $es.Step -Name $es.Name -Status 'Pass' -Detail (Format-Condition $ready -MaxLength 60)
        }
        elseif ($null -eq (Get-Path $es.Object 'metadata.name')) {
            Add-Check -Step $es.Step -Name $es.Name -Status 'Fail' -Detail "the probe read no object at $($es.Namespace)/$($es.Secret) - it is missing, or that one kubectl call failed and the probe substituted an empty document"
        }
        else {
            Add-Check -Step $es.Step -Name $es.Name -Status 'Fail' -Detail (Format-Condition $ready)
        }

        # The expected key set is derived from parameters.json, never listed
        # here - 4b.11's rule. A fourth key added there and not synced is a
        # failure; a key synced that nothing declares is one too.
        $expectedKeys = @(
            foreach ($p in $parameters.parameters) {
                $kb = $p.PSObject.Properties['kubernetes']
                if (-not $kb) { continue }
                foreach ($block in @($kb.Value)) {
                    if ((Get-Field $block 'secretName') -eq $es.Secret -and (Get-Field $block 'namespace') -eq $es.Namespace) {
                        Get-Field $block 'secretKey'
                    }
                }
            }
        ) | Sort-Object
        $actualKeys = @($es.Keys | Sort-Object)
        if ($expectedKeys.Count -gt 0 -and ($expectedKeys -join ',') -eq ($actualKeys -join ',')) {
            Add-Check -Step $es.Step -Name "$($es.Secret) key set" -Status 'Pass' -Detail "$($actualKeys.Count) key(s): $($actualKeys -join ', ')"
        }
        else {
            Add-Check -Step $es.Step -Name "$($es.Secret) key set" -Status 'Fail' -Detail "have [$($actualKeys -join ', ')], parameters.json wants [$($expectedKeys -join ', ')]"
        }
    }

    # -- 8b.5 / 8b.8 / 8b.10: the three backup CronJobs, each against its own
    #    alert threshold. This is the manual form of 8b.11's rules and the
    #    thing that catches a rule which is silently absent().
    function Find-CronJob {
        param([string]$Namespace, [string]$Name)
        return ($cronJobs | Where-Object {
                (Get-Path $_ 'metadata.namespace') -eq $Namespace -and (Get-Path $_ 'metadata.name') -eq $Name
            } | Select-Object -First 1)
    }
    Add-CronJobAgeCheck -Step '8b.5' -Name 'aerie-backup ran recently' -CronJob (Find-CronJob 'aerie' 'aerie-backup') -MaxAgeHours $BackupMaxAgeHours -NowUtc $nowUtc
    Add-CronJobAgeCheck -Step '8b.8' -Name 'aerie-backup-verify ran recently' -CronJob (Find-CronJob 'aerie' 'aerie-backup-verify') -MaxAgeHours ($VerifyMaxAgeDays * 24) -NowUtc $nowUtc
    Add-CronJobAgeCheck -Step '8b.10' -Name 'aerie-critical-daily ran recently' -CronJob (Find-CronJob 'longhorn-system' 'aerie-critical-daily') -MaxAgeHours $BackupMaxAgeHours -NowUtc $nowUtc

    # -- 8a.3: exactly three labelled Volumes, and the set equals the
    #    longhorn-r3 PVCs' volumes in both directions. A volume restored from
    #    a backup comes back without the label and with no error anywhere,
    #    which is why this is a standing assertion rather than a one-time step.
    $labelledVolumes = @($volumes | Where-Object {
            $labels = Get-Path $_ 'metadata.labels'
            $null -ne $labels -and $null -ne $labels.PSObject.Properties[$CriticalVolumeLabel]
        } | ForEach-Object { Get-Path $_ 'metadata.name' })
    $criticalPvcVolumes = @($pvcs | Where-Object { (Get-Path $_ 'spec.storageClassName') -eq $CriticalStorageClass } |
            ForEach-Object { Get-Path $_ 'spec.volumeName' } | Where-Object { $_ })

    $missingLabel = @($criticalPvcVolumes | Where-Object { $labelledVolumes -notcontains $_ })
    $extraLabel = @($labelledVolumes | Where-Object { $criticalPvcVolumes -notcontains $_ })
    if ($criticalPvcVolumes.Count -eq 3 -and $missingLabel.Count -eq 0 -and $extraLabel.Count -eq 0) {
        Add-Check -Step '8a.3' -Name 'Three labelled longhorn-r3 volumes' -Status 'Pass' -Detail "$($labelledVolumes.Count) volume(s), and the label set equals the longhorn-r3 PVC set"
    }
    else {
        $detail = "$($criticalPvcVolumes.Count) longhorn-r3 PVC(s), $($labelledVolumes.Count) labelled volume(s)"
        if ($missingLabel.Count) { $detail += "; unlabelled: $($missingLabel -join ', ')" }
        if ($extraLabel.Count) { $detail += "; labelled but not longhorn-r3: $($extraLabel -join ', ')" }
        Add-Check -Step '8a.3' -Name 'Three labelled longhorn-r3 volumes' -Status 'Fail' -Detail $detail
    }

    # -- 8b.9: the backup target, read off the BackupTarget CR rather than
    #    settings.longhorn.io - 8b.9's own correction, because the setting is
    #    the input and the CR is what Longhorn actually resolved.
    $targetUrl = [string](Get-Path $backupTarget 'spec.backupTargetURL')
    $targetSecret = [string](Get-Path $backupTarget 'spec.credentialSecret')
    $expectedBucket = if ($configData) { [string](Get-Field $configData 'LONGHORN_BACKUP_BUCKET') } else { '' }
    $expectedRegion = if ($configData) { [string](Get-Field $configData 'AWS_REGION') } else { '' }
    $expectedUrl = if ($expectedBucket -and $expectedRegion) { "s3://$expectedBucket@$expectedRegion/" } else { '' }
    if ($expectedUrl -and $targetUrl -eq $expectedUrl -and $targetSecret -eq 'longhorn-backup-target') {
        Add-Check -Step '8b.9' -Name 'Longhorn backup target' -Status 'Pass' -Detail "$targetUrl via $targetSecret"
    }
    else {
        Add-Check -Step '8b.9' -Name 'Longhorn backup target' -Status 'Fail' -Detail "CR says [$targetUrl] / [$targetSecret]; aerie-cluster-config wants [$expectedUrl] / [longhorn-backup-target]"
    }
    if ((Get-Path $backupTarget 'status.available') -eq $true) {
        Add-Check -Step '8b.9' -Name 'Backup target reachable' -Status 'Pass' -Detail 'status.available is true'
    }
    else {
        Add-Check -Step '8b.9' -Name 'Backup target reachable' -Status 'Fail' -Detail 'status.available is not true - Longhorn cannot list the bucket, and every recurring backup silently does nothing'
    }

    # Finding 1's whole argument rests on this one setting, and Longhorn
    # swallows a bad value. The v1 data engine scopes it as {"v1":"true"},
    # which is Longhorn's own shape and not drift - 8b.9 found that.
    $freezeValue = [string](Get-Field $freezeSetting 'value')
    if ($freezeValue -match '"v1"\s*:\s*"true"' -or $freezeValue -eq 'true') {
        Add-Check -Step '8b.9' -Name 'freeze-filesystem-for-snapshot' -Status 'Pass' -Detail $freezeValue
    }
    else {
        Add-Check -Step '8b.9' -Name 'freeze-filesystem-for-snapshot' -Status 'Fail' -Detail "reads [$freezeValue] - without fsfreeze these snapshots are crash-consistent, and finding 1's argument for backing up SQLite this way does not hold"
    }

    # -- 8b.10: the hand-made Secret is gone. Otherwise the kind of cleanup
    #    that gets half-done.
    if ($restoreSecretRaw -eq 'none' -or [string]::IsNullOrWhiteSpace($restoreSecretRaw)) {
        Add-Check -Step '8b.10' -Name 'aerie-pg-restore-restic is gone' -Status 'Pass' -Detail 'not found, as intended'
    }
    else {
        Add-Check -Step '8b.10' -Name 'aerie-pg-restore-restic is gone' -Status 'Fail' -Detail "still exists ($restoreSecretRaw) - run the restore Job against the 'restic' Secret once, then delete it (8b.10, in that order)"
    }

    # -- 8b.11: every rule loaded, and inactive rather than unknown. `inactive`
    #    and `unknown` look nearly identical in the UI and mean opposite things.
    $loadedRules = @()
    foreach ($group in @(Get-Path $rules 'data.groups' | Where-Object { $_ })) {
        foreach ($rule in @(Get-Field $group 'rules' | Where-Object { $_ })) {
            $loadedRules += [pscustomobject]@{
                Name   = [string](Get-Field $rule 'name')
                State  = [string](Get-Field $rule 'state')
                Health = [string](Get-Field $rule 'health')
            }
        }
    }
    if ($expectedAlertNames.Count -eq 0) {
        Add-Check -Step '8b.11' -Name 'Backup alerts loaded' -Status 'Fail' -Detail 'backup.yaml declares no alerts - the manifest did not parse the way this gate reads it'
    }
    else {
        $missingRules = @($expectedAlertNames | Where-Object { $loadedRules.Name -notcontains $_ })
        $badRules = @($loadedRules | Where-Object { $expectedAlertNames -contains $_.Name -and ($_.State -ne 'inactive' -or $_.Health -ne 'ok') } |
                ForEach-Object { "$($_.Name)=$($_.State)/$($_.Health)" })
        if ($missingRules.Count -eq 0 -and $badRules.Count -eq 0) {
            Add-Check -Step '8b.11' -Name 'Backup alerts loaded and inactive' -Status 'Pass' -Detail "$($expectedAlertNames.Count) rule(s), all inactive/ok"
        }
        else {
            $detail = ''
            if ($missingRules.Count) { $detail += "not loaded: $($missingRules -join ', ')" }
            if ($badRules.Count) { $detail += "$(if ($detail) { '; ' })not inactive: $($badRules -join ', ')" }
            Add-Check -Step '8b.11' -Name 'Backup alerts loaded and inactive' -Status 'Fail' -Detail $detail
        }
    }

    # The thresholds this gate uses are constants; this is what fails when
    # backup.yaml's move away from them.
    $thresholdsMatch = ($alertsManifestText -match "$BackupMaxAgeHours \* 3600") -and ($alertsManifestText -match "$VerifyMaxAgeDays \* 24 \* 3600")
    if ($thresholdsMatch) {
        Add-Check -Step '8b.11' -Name 'Alert thresholds match this gate' -Status 'Pass' -Detail "${BackupMaxAgeHours}h daily, ${VerifyMaxAgeDays}d verify"
    }
    else {
        Add-Check -Step '8b.11' -Name 'Alert thresholds match this gate' -Status 'Fail' -Detail "backup.yaml no longer says ${BackupMaxAgeHours} * 3600 and ${VerifyMaxAgeDays} * 24 * 3600 - update this script with it, or the two disagree silently"
    }

    # -- 8b.10's other half: the recurring job has actually produced backups.
    $completedLonghornBackups = @($longhornBackups | Where-Object { (Get-Path $_ 'status.state') -eq 'Completed' })
    if ($completedLonghornBackups.Count -ge 3) {
        Add-Check -Step '8b.10' -Name 'Longhorn volume backups exist' -Status 'Pass' -Detail "$($completedLonghornBackups.Count) completed backup object(s)"
    }
    else {
        Add-Check -Step '8b.10' -Name 'Longhorn volume backups exist' -Status 'Fail' -Detail "$($completedLonghornBackups.Count) completed backup object(s) - three volumes are labelled, so fewer than three means one is not being backed up"
    }

    # -- 8b.6 and 8b.5/8b.7: what is actually in the repositories.
    if ($null -eq $resticProbeOutput) {
        foreach ($name in @('cutover-final in both repos', 'Newest snapshot holds all three files')) {
            Add-Check -Step '8b.6' -Name $name -Status 'Fail' -Detail "not evaluated: $resticProbeError"
        }
    }
    else {
        # The log is one block per repository: a `--- repo <path>` marker, then
        # the three answers. Split on the marker so a two-repo run is checked
        # as two independent results rather than as one merged blob.
        $blocks = @($resticProbeOutput -split '(?m)^--- repo ' | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*resticend' })
        $repoResults = New-Object Collections.Generic.List[psobject]
        foreach ($block in $blocks) {
            $lines = @($block -split "`r?`n")
            $repoName = $lines[0].Trim()
            if (-not $repoName) { continue }
            $section = ''
            $cutover = @(); $files = @(); $paramCount = $null
            foreach ($line in $lines[1..($lines.Count - 1)]) {
                $trimmed = $line.Trim()
                if ($trimmed -eq '--- cutover') { $section = 'cutover'; continue }
                if ($trimmed -eq '--- files') { $section = 'files'; continue }
                if ($trimmed -eq '--- params') { $section = 'params'; continue }
                if ($trimmed -eq '--- resticend' -or -not $trimmed) { continue }
                switch ($section) {
                    'cutover' { $cutover += $trimmed }
                    'files' { $files += $trimmed }
                    'params' { if ($null -eq $paramCount) { $paramCount = $trimmed } }
                }
            }
            $repoResults.Add([pscustomobject]@{
                    Repo       = $repoName
                    Cutover    = ($cutover -join '')
                    Files      = $files
                    ParamCount = $paramCount
                })
        }

        if ($repoResults.Count -lt 2) {
            Add-Check -Step '8b.6' -Name 'cutover-final in both repos' -Status 'Fail' -Detail "the probe reported $($repoResults.Count) repository block(s), not 2"
            Add-Check -Step '8b.5' -Name 'Newest snapshot holds all three files' -Status 'Fail' -Detail "the probe reported $($repoResults.Count) repository block(s), not 2"
        }
        else {
            # An empty result is a failure here, and the empty *repository*
            # case is not a special case worth carving out: a repository with
            # no cutover-final snapshot on an installation that had one is
            # exactly what 8b.6 exists to catch. A fresh installation that
            # never had one will fail this check and should read 8b.6 before
            # deleting it.
            $withoutCutover = @($repoResults | Where-Object { $_.Cutover -eq '[]' -or $_.Cutover -eq 'ERROR' -or -not $_.Cutover })
            if ($withoutCutover.Count -eq 0) {
                Add-Check -Step '8b.6' -Name 'cutover-final in both repos' -Status 'Pass' -Detail "$($repoResults.Count) repo(s), each holding the tagged snapshot"
            }
            else {
                Add-Check -Step '8b.6' -Name 'cutover-final in both repos' -Status 'Fail' -Detail "missing from: $(($withoutCutover | ForEach-Object { $_.Repo }) -join ', ')"
            }

            $incomplete = @($repoResults | Where-Object {
                    $names = @($_.Files | ForEach-Object { ($_ -split '/')[-1] })
                    @($ExpectedSnapshotFiles | Where-Object { $names -notcontains $_ }).Count -gt 0
                })
            if ($incomplete.Count -eq 0) {
                Add-Check -Step '8b.5' -Name 'Newest snapshot holds all three files' -Status 'Pass' -Detail ($ExpectedSnapshotFiles -join ', ')
            }
            else {
                Add-Check -Step '8b.5' -Name 'Newest snapshot holds all three files' -Status 'Fail' -Detail "incomplete in: $(($incomplete | ForEach-Object { $_.Repo }) -join ', ')"
            }

            # 8b.7: the export is only an answer if it captured the tree. The
            # expectation is the count of required parameters in
            # parameters.json - the two optional ones may legitimately not be
            # seeded, so this is a floor rather than an equality.
            $requiredCount = @($parameters.parameters | Where-Object { (Get-Field $_ 'required') -eq $true }).Count
            $shortExports = @($repoResults | Where-Object {
                    $n = 0
                    -not ([int]::TryParse($_.ParamCount, [ref]$n)) -or $n -lt $requiredCount
                })
            if ($shortExports.Count -eq 0) {
                Add-Check -Step '8b.7' -Name 'Parameter export covers the tree' -Status 'Pass' -Detail "$($repoResults[0].ParamCount) parameter(s), $requiredCount required in parameters.json"
            }
            else {
                Add-Check -Step '8b.7' -Name 'Parameter export covers the tree' -Status 'Fail' -Detail "short or unreadable in: $(($shortExports | ForEach-Object { "$($_.Repo) ($($_.ParamCount))" }) -join ', '); parameters.json declares $requiredCount required"
            }
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Portability'
    # ---------------------------------------------------------------- #

    # design.md's verification rule, scoped to this phase's own values -
    # LONGHORN_BACKUP_BUCKET, the restic repository string and SHARE_HOST are
    # per-installation and new here, so nothing upstream has ever grepped for
    # them. DOMAIN and the VIP are Test-ClusterPlatform.ps1's, deliberately not
    # repeated.
    $deployFiles = @(Get-ChildItem -Path $DeployPath -Recurse -File)
    $deployRoot = (Resolve-Path $DeployPath).Path
    foreach ($key in @('LONGHORN_BACKUP_BUCKET', 'RESTIC_S3_REPOSITORY', 'SHARE_HOST')) {
        $value = if ($configData) { [string](Get-Field $configData $key) } else { '' }
        if (-not $value -or $value.Length -lt 4) {
            Add-Check -Step '8b.16' -Name "No $key literal in deploy/" -Status 'Fail' -Detail 'aerie-cluster-config does not carry this key, so the check cannot be made'
            continue
        }
        $hits = @($deployFiles | Select-String -SimpleMatch -Pattern $value -ErrorAction SilentlyContinue)
        if ($hits.Count -gt 0) {
            $leaks = @($hits | ForEach-Object {
                    $relative = $_.Path.Substring($deployRoot.Length).TrimStart('\', '/')
                    "deploy/$relative`:$($_.LineNumber)"
                })
            Add-Check -Step '8b.16' -Name "No $key literal in deploy/" -Status 'Fail' -Detail "$($leaks -join '; '). Belongs as a `${$key} substitution - see docs/ethos.md"
        }
        else {
            Add-Check -Step '8b.16' -Name "No $key literal in deploy/" -Status 'Pass' -Detail "$($deployFiles.Count) file(s) checked"
        }
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- #
Write-Stage 'Report'
# ---------------------------------------------------------------- #

$passed = @($script:Checks | Where-Object { $_.Result -eq 'Pass' }).Count
$warned = @($script:Checks | Where-Object { $_.Result -eq 'Warn' }).Count
$failed = @($script:Checks | Where-Object { $_.Result -eq 'Fail' }).Count
$elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)

Write-Host ''
$script:Checks |
    Select-Object Step, Check, Result, @{ Name = 'Detail'; Expression = { if ($_.Detail.Length -gt 90) { $_.Detail.Substring(0, 89) + [char]0x2026 } else { $_.Detail } } } |
    Format-Table -AutoSize | Out-String -Width 220 | ForEach-Object { Write-Host $_.TrimEnd() }

if ($failed -gt 0) {
    Write-Host ''
    Write-Host 'Failures in full:' -ForegroundColor Red
    foreach ($check in ($script:Checks | Where-Object { $_.Result -eq 'Fail' })) {
        Write-Host "  [$($check.Step)] $($check.Check)" -ForegroundColor Red
        Write-Host "      $($check.Detail)"
    }
}

if ($env:GITHUB_STEP_SUMMARY) {
    $tick = [char]0x60
    $lines = @(
        "## Phase 8 backup gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { [char]0x2705 } 'Warn' { [char]0x26A0 } default { [char]0x274C } }
        $detail = ($check.Detail -replace '\|', '\|')
        $lines += "| $($check.Step) | $($check.Check) | $mark $($check.Result) | $detail |"
    }
    $lines += @(
        ''
        '_The cluster plan, Phase 8b.16. Read-only except for one short-lived Job that runs three read-only restic commands._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 8 backup gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names the step it belongs to; docs/plans/swarm/phase-8-backup-v2.md 8b has the reasoning for each.'
    exit 1
}

Write-Host "Phase 8 backup gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
