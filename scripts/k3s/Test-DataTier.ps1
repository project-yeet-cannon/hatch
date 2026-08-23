<#
.SYNOPSIS
    Asserts every *Exit* criterion of the cluster plan Phase 4b in one run,
    the same shape scripts/k3s/Test-ClusterPlatform.ps1 established for Phase
    3b.13. "Phase 4 is done" as a command rather than as a memory.

.DESCRIPTION
    Phase 4b.11. Steps 4b.1-4b.10 each carry an *Exit* line. This runs the
    ones a live cluster can still prove - 4b.1's backup-cycle exit and 4b.9's
    restore/hand-comparison are one-time proofs a re-run cannot repeat
    honestly, so they are out of scope here - and reports one table.

    Same three properties Test-ClusterPlatform.ps1 states and relies on:

    **It does not stop at the first failure.** Nothing here writes anything,
    so continuing past a surprise costs nothing and a table of twelve
    findings is worth more than the first one.

    **A check it cannot evaluate is a failure, not a skip.** An absent
    object or an unparseable response is "not proven", and a gate that reports
    that as anything but failure can be satisfied by a cluster that is off.

    **Expectations come from the cluster and the repository, not from
    parameters.** POSTGRES_INSTANCES comes from the live aerie-cluster-config
    ConfigMap; the expected ExternalSecret set comes from
    scripts/secrets/parameters.json; dataDurability and the backup schedule
    come from the committed manifests. Nothing here is a number to remember
    to pass, so a run months from now checks the same things this one does
    even after those files change.

    Stages:
      1. Preflight - the SSH key resolves, the client is present, the node
                      answers 22, and the committed maps and manifests parse.
      2. Probe      - one round trip collects every object the checks below
                      reason about, including a local psql exec inside the
                      primary pod for the two things only SQL can answer:
                      the qrtz_* table count and the aerie-table sanity
                      counts.
      3. Checks     - 4b.2-4b.10, evaluated against that snapshot.
      4. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here is cluster
    state, the same as Test-ClusterPlatform.ps1.

.EXAMPLE
    .\Test-DataTier.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$MapPath = (Join-Path $PSScriptRoot 'cluster-config.json'),
    [string]$ParametersPath = (Join-Path $PSScriptRoot '..\secrets\parameters.json'),
    [string]$DeployPath = (Join-Path $PSScriptRoot '..\..\deploy'),
    [string]$ClusterManifestPath = (Join-Path $PSScriptRoot '..\..\deploy\cluster\data\cluster\cluster.yaml'),
    [string]$ScheduledBackupManifestPath = (Join-Path $PSScriptRoot '..\..\deploy\cluster\data\schema\scheduledbackup.yaml'),

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# What 4b.8's DDL guarantees: eleven qrtz_* tables, named once so the check
# and the message can't drift apart.
$ExpectedQuartzTables = 11

$script:StageNumber = 0
function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Get-Field {
    <#
    .SYNOPSIS
        Reads a property off a ConvertFrom-Json object, returning $null when
        it's absent instead of throwing under Set-StrictMode.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$Object,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Path {
    <#
    .SYNOPSIS
        Walks a dotted path of property names, returning $null the moment any
        link is missing. See Test-ClusterPlatform.ps1's copy for why this
        matters for a gate specifically: an absent path and an absent value
        both mean "not proven".
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$Object,
        [Parameter(Mandatory)][string]$Path
    )
    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        $current = Get-Field $current $segment
    }
    return $current
}

function Get-Items {
    <#
    .SYNOPSIS
        The `.items` of a kubectl list, as an array that is empty rather than
        $null when there are none. See Test-ClusterPlatform.ps1's copy for
        why every call site wraps this in @( ) and why the comma operator is
        wrong here.
    #>
    param([Parameter(Mandatory)][AllowNull()]$List)
    return @(Get-Field $List 'items' | Where-Object { $_ })
}

function Get-Condition {
    <#
    .SYNOPSIS
        The named entry of an object's status.conditions, or $null.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Type)
    $conditions = @(Get-Path $Object 'status.conditions' | Where-Object { $_ })
    return ($conditions | Where-Object { (Get-Field $_ 'type') -eq $Type } | Select-Object -First 1)
}

function Get-ReadyCondition {
    <#
    .SYNOPSIS
        The `Ready` entry of an object's status.conditions, or $null.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Object)
    return (Get-Condition $Object 'Ready')
}

function Format-Condition {
    param([Parameter(Mandatory)][AllowNull()]$Condition, [int]$MaxLength = 160)
    if ($null -eq $Condition) { return 'no Ready condition' }
    $reason = [string](Get-Field $Condition 'reason')
    $message = [string](Get-Field $Condition 'message') -replace '\s+', ' '
    $text = (@($reason, $message) | Where-Object { $_ }) -join ': '
    if (-not $text) { $text = [string](Get-Field $Condition 'status') }
    if ($text.Length -gt $MaxLength) { $text = $text.Substring(0, $MaxLength - 1) + '…' }
    return $text
}

function Get-ProbeSection {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$Name
    )
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
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$Name
    )
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

function Add-ObjectReadyCheck {
    <#
    .SYNOPSIS
        The object exists and its Ready condition is True. See
        Test-ClusterPlatform.ps1's copy for why a suspended object is
        checked before its condition rather than after - it keeps whatever
        condition it last had, which is history, not current state.
    #>
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowNull()]$Object,
        [string]$MissingDetail = 'not found'
    )
    if ($null -eq $Object) {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail $MissingDetail
        return $false
    }
    if ((Get-Path $Object 'spec.suspend') -eq $true) {
        Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail 'suspended - its Ready condition describes the last reconciliation, not the current state'
        return $false
    }
    $condition = Get-ReadyCondition $Object
    if ((Get-Field $condition 'status') -eq 'True') {
        Add-Check -Step $Step -Name $Name -Status 'Pass' -Detail (Format-Condition $condition -MaxLength 60)
        return $true
    }
    Add-Check -Step $Step -Name $Name -Status 'Fail' -Detail (Format-Condition $condition)
    return $false
}

function ConvertTo-UtcDateTime {
    <#
    .SYNOPSIS
        One Kubernetes timestamp as a UTC [DateTime], whatever shape
        ConvertFrom-Json handed it back in - or $null if it is not a
        timestamp at all.

    .DESCRIPTION
        ConvertFrom-Json does not leave an RFC 3339 string as a string: it
        deserializes it into a [DateTime]. Casting that back to [string] -
        the obvious way to read `status.startedAt` - renders it in the
        *current culture's* format with no zone at all ('08/23/2026
        02:00:02'), and re-parsing that assumes local time. On a runner four
        hours behind UTC that turned a backup taken four hours ago into one
        taken now, so 4b.10's age check could not see a stale backup within a
        whole UTC offset of its threshold. The same cast also mis-sorts:
        'MM/dd/yyyy' orders by month before year, so "the newest Backup"
        chosen by string comparison is only correct within one December.
        Found while building scripts/k3s/Test-Cutover.ps1 (7c.11), which
        carries the same helper.

        Kind is handled rather than assumed: PowerShell 7 returns Kind=Utc
        for a 'Z' input, Windows PowerShell 5.1 has historically returned the
        same instant as Kind=Local, and the workflow wrapper runs 5.1.
        Unspecified is read as UTC - every timestamp this script reads comes
        from the Kubernetes API server, which emits nothing else.
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

function Get-YamlScalar {
    <#
    .SYNOPSIS
        Pulls a single `key: value` scalar out of a committed manifest by
        line-matching, not a YAML parser.

    .DESCRIPTION
        This script has exactly two things to read out of a manifest -
        dataDurability and a cron schedule - both single quoted or bare
        scalars on their own line. A real YAML parser is not part of Windows
        PowerShell 5.1's base install, and the manifests it reads are this
        repository's own, committed, and covered by ci.yml's kustomize build
        - so a line match is proportionate here in a way it would not be
        against arbitrary input.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Key
    )
    if (-not (Test-Path $Path -PathType Leaf)) { return $null }
    $match = Select-String -Path $Path -Pattern "^\s*${Key}:\s*(.+?)\s*$" | Select-Object -First 1
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value.Trim('"', "'")
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    foreach ($path in @($MapPath, $ParametersPath)) {
        if (-not (Test-Path $path -PathType Leaf)) { $failures.Add("Not found: '$path'. Run this from a checkout of the repository the cluster reconciles from.") }
    }
    if (-not (Test-Path $DeployPath -PathType Container)) {
        $failures.Add("Not found: '$DeployPath'.")
    }
    foreach ($path in @($ClusterManifestPath, $ScheduledBackupManifestPath)) {
        if (-not (Test-Path $path -PathType Leaf)) { $failures.Add("Not found: '$path'. The dataDurability and schedule checks read their expectation from this committed file rather than hardcoding it.") }
    }

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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-data-tier-gate'
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
    $expectedDurability = Get-YamlScalar -Path $ClusterManifestPath -Key 'dataDurability'
    $scheduleExpression = Get-YamlScalar -Path $ScheduledBackupManifestPath -Key 'schedule'

    Write-Host "Cluster:  $IPAddress"
    Write-Host "Repo:     $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host 'Preflight OK. Nothing below writes to the cluster - the psql query in the probe is read-only.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-data-tier-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip, the same discipline Test-ClusterPlatform.ps1 uses and
    # documents at length: every command falls back to `echo {}` on an
    # unregistered type, so a half-built data tier answers "no items" rather
    # than aborting the whole probe on its first missing CRD. Also inherited
    # from there: single quotes only, no double quotes anywhere in
    # $probeScript - Invoke-NodeSsh refuses a command containing one, because
    # Windows PowerShell 5.1 lets ssh.exe strip it and run a subtly different
    # script on the node.
    #
    # The qrtz_* count is the one thing that needs two dependent reads in one
    # trip: the primary's pod name isn't known until the Cluster is read, so
    # the shell captures it and execs psql against whatever it got - see the
    # quartztables line below for what an empty $PRIMARY does instead of the
    # `[ -n ... ]` guard that would need a forbidden character to write.
    # psql runs as the `postgres` role over the pod's local unix socket -
    # available for local, read-only exec regardless of
    # spec.enableSuperuserAccess, which only gates a *remote* superuser
    # credential (cluster.yaml leaves it at its default, false, on purpose -
    # see that file). Nothing here writes, and nothing prints a credential:
    # only counts leave the pod.
    $probeScript = @(
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- cluster\n'''
        'sudo k3s kubectl -n aerie get cluster aerie-pg -o json 2>/dev/null || echo {}'
        'printf ''\n--- pods\n'''
        'sudo k3s kubectl -n aerie get pods -l cnpg.io/cluster=aerie-pg -o json 2>/dev/null || echo {}'
        'printf ''\n--- database\n'''
        'sudo k3s kubectl -n aerie get databases.postgresql.cnpg.io aerie-pg-quartz -o json 2>/dev/null || echo {}'
        'printf ''\n--- ddljob\n'''
        'sudo k3s kubectl -n aerie get job aerie-quartz-ddl -o json 2>/dev/null || echo {}'
        'printf ''\n--- backups\n'''
        'sudo k3s kubectl -n aerie get backups.postgresql.cnpg.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- objectstore\n'''
        'sudo k3s kubectl -n aerie get objectstores.barmancloud.cnpg.io aerie-pg-wal -o json 2>/dev/null || echo {}'
        'printf ''\n--- plugindeploy\n'''
        'sudo k3s kubectl -n cnpg-system get deployment plugin-barman-cloud -o json 2>/dev/null || echo {}'
        'printf ''\n--- plugincerts\n'''
        'sudo k3s kubectl -n cnpg-system get certificates.cert-manager.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- externalsecret\n'''
        'sudo k3s kubectl -n aerie get externalsecrets.external-secrets.io cnpg-wal-s3 -o json 2>/dev/null || echo {}'
        # Key *presence*, never the Secret's -o json or a dynamic key
        # iteration over it - both would put base64-encoded credential
        # material into this run log, or need a jsonpath range/quoting the
        # no-double-quotes rule above rules out cleanly. Each line below
        # pipes a value through `wc -c` on the node itself, so only a byte
        # count - never the value - reaches this script's stdout. The three
        # names are 4b.3/4b.5's own (cnpg-wal-s3's declared keys); the check
        # below cross-references them against parameters.json so a fourth key
        # added there without a matching line here is reported as unproven
        # rather than silently ignored.
        'printf ''\n--- secretkeys\n'''
        'printf ''access-key-id ''; sudo k3s kubectl -n aerie get secret cnpg-wal-s3 -o jsonpath={.data.access-key-id} 2>/dev/null | wc -c'
        'printf ''secret-access-key ''; sudo k3s kubectl -n aerie get secret cnpg-wal-s3 -o jsonpath={.data.secret-access-key} 2>/dev/null | wc -c'
        'printf ''region ''; sudo k3s kubectl -n aerie get secret cnpg-wal-s3 -o jsonpath={.data.region} 2>/dev/null | wc -c'
        'printf ''\n--- quartztables\n'''
        # No double quotes anywhere in this line - see the note above this
        # probe script. The SQL string's own embedded quotes use the standard POSIX sh
        # trick (close, escaped literal quote, reopen) rather than double
        # quotes - verified by hand against a real `sh` before landing here,
        # since a subtly wrong escape here fails silently as an empty count
        # rather than a script error. No `[ -n ... ]` guard on $PRIMARY
        # either, for the same reason: an unquoted empty-string test is a
        # classic footgun ([ -n ] alone is always true), and quoting it needs
        # the same forbidden character. An empty $PRIMARY just makes the
        # exec argument list malformed, which kubectl reports as an error
        # this line's trailing `|| true` and the empty-result check below
        # both already handle.
        'PRIMARY=$(sudo k3s kubectl -n aerie get cluster aerie-pg -o jsonpath={.status.currentPrimary} 2>/dev/null || true); sudo k3s kubectl -n aerie exec $PRIMARY -c postgres -- psql -U postgres -d quartz -Atc ''select count(*) from information_schema.tables where table_schema=''\''''public''\'''' and table_name like ''\''''qrtz\_%''\'''''' 2>/dev/null | tail -1 || true'
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
    $cluster = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'cluster'
    $pods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pods'))
    $database = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'database'
    $ddlJob = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'ddljob'
    $backups = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'backups'))
    $objectStore = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'objectstore'
    $pluginDeploy = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'plugindeploy'
    $pluginCerts = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'plugincerts'))
    $externalSecret = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'externalsecret'
    # Each line is "<key> <byte count>" - a key counts as present when its
    # value's byte count is greater than zero. `wc -c`'s output is padded
    # with leading spaces on some platforms, so this matches the trailing
    # digits rather than assuming a fixed column.
    $secretKeys = @((Get-ProbeSection -Output $probe.StdOut -Name 'secretkeys') -split "`n" | ForEach-Object {
            if ($_ -match '^(\S+)\s+(\d+)\s*$' -and [int]$Matches[2] -gt 0) { $Matches[1] }
        } | Where-Object { $_ })
    $quartzTableCountRaw = (Get-ProbeSection -Output $probe.StdOut -Name 'quartztables').Trim()

    $configData = Get-Field $clusterConfig 'data'
    $expectedInstances = if ($configData) { [string](Get-Field $configData 'POSTGRES_INSTANCES') } else { $null }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- 4b.6 (part 1): Cluster ready at the configured instance count -----
    if ([string]::IsNullOrWhiteSpace($expectedInstances)) {
        Add-Check -Step '4b.6' -Name 'Cluster instance count' -Status 'Fail' -Detail 'POSTGRES_INSTANCES is missing or empty in aerie-cluster-config - dispatch Provision 4 (4b.7)'
    }
    elseif ($null -eq $cluster -or -not (Get-Field $cluster 'status')) {
        Add-Check -Step '4b.6' -Name 'Cluster aerie-pg' -Status 'Fail' -Detail 'not found in namespace aerie'
    }
    else {
        $readyInstances = [string](Get-Path $cluster 'status.readyInstances')
        $phase = [string](Get-Path $cluster 'status.phase')
        if ($readyInstances -eq $expectedInstances -and $phase -eq 'Cluster in healthy state') {
            Add-Check -Step '4b.6' -Name 'Cluster aerie-pg' -Status 'Pass' -Detail "$readyInstances/$expectedInstances ready, phase: $phase"
        }
        else {
            Add-Check -Step '4b.6' -Name 'Cluster aerie-pg' -Status 'Fail' -Detail "readyInstances=$readyInstances (want $expectedInstances), phase: $phase"
        }
    }

    # --- 4b.6 (part 2): required anti-affinity actually applied -------------
    # A `required` rule that silently did not apply (wrong label selector, a
    # topologyKey mismatch) looks identical to one that did, right up until a
    # node dies and takes two instances with it - so this is checked
    # directly against where the pods actually landed, not against the spec
    # that asked for it. No further label filtering here: the probe's own
    # `-l cnpg.io/cluster=aerie-pg` selector already scoped $pods to this
    # Cluster's instance pods and nothing else - a second filter keyed on a
    # label name containing a dot would need Get-Path to walk a literal
    # 'cnpg.io/instanceRole' segment, which it can't: it splits on every '.'
    # with no escaping, the same trap Test-ClusterPlatform.ps1's Get-MapValue
    # exists to avoid.
    $instancePods = $pods
    if ($instancePods.Count -eq 0) {
        Add-Check -Step '4b.6' -Name 'Instances on distinct nodes' -Status 'Fail' -Detail 'no instance pods found (label cnpg.io/cluster=aerie-pg)'
    }
    else {
        $nodeNames = @($instancePods | ForEach-Object { [string](Get-Path $_ 'spec.nodeName') } | Where-Object { $_ })
        $distinct = @($nodeNames | Select-Object -Unique)
        if ($nodeNames.Count -ne $instancePods.Count) {
            Add-Check -Step '4b.6' -Name 'Instances on distinct nodes' -Status 'Fail' -Detail "$($instancePods.Count - $nodeNames.Count) instance pod(s) not yet scheduled (no spec.nodeName)"
        }
        elseif ($distinct.Count -ne $instancePods.Count) {
            Add-Check -Step '4b.6' -Name 'Instances on distinct nodes' -Status 'Fail' -Detail "$($instancePods.Count) pod(s) across only $($distinct.Count) node(s): $($nodeNames -join ', ') - required anti-affinity did not apply"
        }
        else {
            Add-Check -Step '4b.6' -Name 'Instances on distinct nodes' -Status 'Pass' -Detail "$($instancePods.Count) pod(s) across $($distinct.Count) node(s)"
        }
    }

    # --- 4b.6 (part 3): dataDurability matches the committed manifest ------
    if (-not $expectedDurability) {
        Add-Check -Step '4b.6' -Name 'synchronous.dataDurability' -Status 'Fail' -Detail "couldn't read a dataDurability value out of $ClusterManifestPath"
    }
    else {
        $liveDurability = [string](Get-Path $cluster 'spec.postgresql.synchronous.dataDurability')
        if ($liveDurability -eq $expectedDurability) {
            Add-Check -Step '4b.6' -Name 'synchronous.dataDurability' -Status 'Pass' -Detail $liveDurability
        }
        else {
            Add-Check -Step '4b.6' -Name 'synchronous.dataDurability' -Status 'Fail' -Detail "cluster reports '$liveDurability', manifest says '$expectedDurability' - a commit hasn't reconciled, or the live Cluster was hand-edited"
        }
    }

    # --- 4b.4/4b.5/4b.6: continuous archiving --------------------------
    # CNPG 1.30's Cluster has no status.lastArchivedWALTime/lastFailedWALTime
    # scalar fields at all - that shape is from older docs. The live signal
    # is a status.conditions entry of type ContinuousArchiving, which the
    # operator flips to False (with a message) the moment an archive attempt
    # fails, and back to True on the next success - so, unlike a timestamp,
    # it never needs an age/staleness judgment call on a quiet cluster.
    $archivingCondition = Get-Condition $cluster 'ContinuousArchiving'
    if ($null -eq $archivingCondition) {
        Add-Check -Step '4b.6' -Name 'Continuous archiving' -Status 'Fail' -Detail 'no ContinuousArchiving condition reported - no WAL has ever archived successfully. Check the ObjectStore credential (4b.3) and the plugin (4b.4)'
    }
    elseif ((Get-Field $archivingCondition 'status') -eq 'True') {
        Add-Check -Step '4b.6' -Name 'Continuous archiving' -Status 'Pass' -Detail (Format-Condition $archivingCondition -MaxLength 60)
    }
    else {
        Add-Check -Step '4b.6' -Name 'Continuous archiving' -Status 'Fail' -Detail (Format-Condition $archivingCondition)
    }

    # --- 4b.10: newest Backup completed and recent ---------------------
    if ($backups.Count -eq 0) {
        Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Fail' -Detail 'no Backup objects found - the ScheduledBackup has not produced one yet'
    }
    else {
        # Sorted and aged on the real instant - see ConvertTo-UtcDateTime
        # above for why a [string] cast of status.startedAt is neither.
        $newest = $backups |
            Sort-Object { $moment = ConvertTo-UtcDateTime (Get-Path $_ 'status.startedAt'); if ($null -eq $moment) { [DateTime]::MinValue } else { $moment } } -Descending |
            Select-Object -First 1
        $phase = [string](Get-Path $newest 'status.phase')
        $startedAtRaw = Get-Path $newest 'status.startedAt'
        $started = ConvertTo-UtcDateTime $startedAtRaw
        if ($phase -ne 'completed') {
            Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Fail' -Detail "phase is '$phase', not completed - $(Get-Path $newest 'metadata.name')"
        }
        elseif ($null -eq $started) {
            Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Fail' -Detail "completed, but status.startedAt ('$startedAtRaw') isn't a timestamp"
        }
        else {
            $age = (Get-Date).ToUniversalTime() - $started
            # "Younger than the schedule interval" per 4b.11 - derived from
            # the committed six-field cron rather than a parameter. This gate
            # only recognises the shape 4b.10 actually uses (a fixed daily
            # time: day-of-month, month and day-of-week all `*`); anything
            # else is reported as unproven rather than guessed at.
            $cronFields = @($scheduleExpression -split '\s+' | Where-Object { $_ })
            if ($cronFields.Count -eq 6 -and $cronFields[3] -eq '*' -and $cronFields[4] -eq '*' -and $cronFields[5] -eq '*') {
                if ($age.TotalHours -le 25) {
                    Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Pass' -Detail "completed $([math]::Round($age.TotalHours, 1))h ago (daily schedule)"
                }
                else {
                    Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Fail' -Detail "completed, but $([math]::Round($age.TotalHours, 1))h old against a daily ('$scheduleExpression') schedule"
                }
            }
            else {
                Add-Check -Step '4b.10' -Name 'Newest Backup' -Status 'Warn' -Detail "completed $([math]::Round($age.TotalHours, 1))h ago; schedule '$scheduleExpression' isn't a plain daily expression this gate knows how to age-check"
            }
        }
    }

    # --- 4b.8: quartz Database ready and eleven tables ------------------
    # CNPG's Database CRD carries status.applied, not a Ready condition - its
    # status is only ever { applied, observedGeneration, message } - so this
    # normalises into the same shape Add-ObjectReadyCheck expects rather than
    # duplicating its missing/suspend logic for one CRD.
    $databaseNormalized = $null
    if ($database -and (Get-Path $database 'status.applied') -eq $true) {
        $databaseNormalized = [pscustomobject]@{ status = [pscustomobject]@{ conditions = @([pscustomobject]@{ type = 'Ready'; status = 'True'; reason = 'Applied' }) } }
    }
    $databaseMissingDetail = if ($database) { "not applied - status.applied=$(Get-Path $database 'status.applied'), message=$(Get-Path $database 'status.message')" } else { 'not found' }
    [void](Add-ObjectReadyCheck -Step '4b.8' -Name 'Database aerie-pg-quartz' -Object $databaseNormalized -MissingDetail $databaseMissingDetail)

    # Jobs carry a Complete condition, not Ready - normalised into the same
    # shape Add-ObjectReadyCheck expects so this reuses it rather than
    # duplicating its suspend/condition logic.
    #
    # The condition itself must be a [pscustomobject], not a bare hashtable:
    # Get-Field reads via .PSObject.Properties[$Name], and a [hashtable]'s
    # dictionary keys never show up there - only the Hashtable type's own
    # members do (Keys, Values, Count...). A bare @{ type = 'Ready'; ... }
    # here makes Get-ReadyCondition's `type -eq 'Ready'` filter silently miss
    # every time, which is what produced "no Ready condition" for a Job that
    # had, in fact, completed.
    $ddlJobNormalized = $null
    if ($ddlJob -and [int](Get-Path $ddlJob 'status.succeeded') -ge 1) {
        $ddlJobNormalized = [pscustomobject]@{ status = [pscustomobject]@{ conditions = @([pscustomobject]@{ type = 'Ready'; status = 'True'; reason = 'Complete' }) } }
    }
    $ddlJobMissingDetail = if ($ddlJob) { "not complete - succeeded=$(Get-Path $ddlJob 'status.succeeded'), failed=$(Get-Path $ddlJob 'status.failed')" } else { 'not found' }
    [void](Add-ObjectReadyCheck -Step '4b.8' -Name 'Job aerie-quartz-ddl' -Object $ddlJobNormalized -MissingDetail $ddlJobMissingDetail)

    if ([string]::IsNullOrWhiteSpace($quartzTableCountRaw)) {
        Add-Check -Step '4b.8' -Name 'qrtz_* table count' -Status 'Fail' -Detail 'no primary reported, or the exec failed - see status.currentPrimary on the Cluster'
    }
    else {
        $count = 0
        if ([int]::TryParse($quartzTableCountRaw, [ref]$count) -and $count -eq $ExpectedQuartzTables) {
            Add-Check -Step '4b.8' -Name 'qrtz_* table count' -Status 'Pass' -Detail "$count of $ExpectedQuartzTables"
        }
        else {
            Add-Check -Step '4b.8' -Name 'qrtz_* table count' -Status 'Fail' -Detail "found $quartzTableCountRaw, expected $ExpectedQuartzTables"
        }
    }

    # --- 4b.4: plugin Deployment rolled out and both certificates Ready -
    if ($null -eq $pluginDeploy -or -not (Get-Field $pluginDeploy 'status')) {
        Add-Check -Step '4b.4' -Name 'Deployment plugin-barman-cloud' -Status 'Fail' -Detail 'not found in namespace cnpg-system'
    }
    else {
        $desired = [int](Get-Path $pluginDeploy 'spec.replicas')
        $available = [int](Get-Path $pluginDeploy 'status.availableReplicas')
        if ($desired -gt 0 -and $available -eq $desired) {
            Add-Check -Step '4b.4' -Name 'Deployment plugin-barman-cloud' -Status 'Pass' -Detail "$available/$desired available"
        }
        else {
            Add-Check -Step '4b.4' -Name 'Deployment plugin-barman-cloud' -Status 'Fail' -Detail "$available/$desired available"
        }
    }
    if ($pluginCerts.Count -eq 0) {
        Add-Check -Step '4b.4' -Name 'Plugin certificates' -Status 'Fail' -Detail 'no Certificates found in namespace cnpg-system - the chart mints these itself (certificate.create*), so their absence means the release never rendered them, not that 3b.10 owns them'
    }
    else {
        $notReady = @($pluginCerts | Where-Object { (Get-Field (Get-ReadyCondition $_) 'status') -ne 'True' } | ForEach-Object { Get-Path $_ 'metadata.name' })
        if ($notReady.Count -gt 0) {
            Add-Check -Step '4b.4' -Name 'Plugin certificates' -Status 'Fail' -Detail "not Ready: $($notReady -join ', ')"
        }
        else {
            Add-Check -Step '4b.4' -Name 'Plugin certificates' -Status 'Pass' -Detail "$($pluginCerts.Count) Certificate(s) Ready"
        }
    }

    # --- 4b.5: ObjectStore exists ---------------------------------------
    if ($null -eq $objectStore -or -not (Get-Path $objectStore 'metadata.name')) {
        Add-Check -Step '4b.5' -Name 'ObjectStore aerie-pg-wal' -Status 'Fail' -Detail 'not found in namespace aerie'
    }
    else {
        # No meaningful status of its own until a Cluster uses it - per the
        # phase doc, the archiving check above is what actually proves the
        # credential. This only proves the object exists.
        Add-Check -Step '4b.5' -Name 'ObjectStore aerie-pg-wal' -Status 'Pass'
    }

    # --- 4b.3: cnpg-wal-s3 SecretSynced with three keys ------------------
    if (Add-ObjectReadyCheck -Step '4b.3' -Name 'ExternalSecret cnpg-wal-s3' -Object $externalSecret) {
        $expectedKeys = @()
        foreach ($parameter in @(Get-Field $parameters 'parameters' | Where-Object { $_ })) {
            $kubernetes = Get-Field $parameter 'kubernetes'
            if ($null -eq $kubernetes) { continue }
            # One block or an array of them, per New-ExternalSecrets.ps1: a
            # value can land in several namespaces. None of the cnpg-wal-s3
            # three do today, and an array read as a single object matches
            # nothing rather than erroring - so the day one of them gains a
            # second target, this would quietly drop its key from the expected
            # set and report the *cluster* as wrong.
            foreach ($target in @($kubernetes)) {
                if ((Get-Field $target 'namespace') -eq 'aerie' -and (Get-Field $target 'secretName') -eq 'cnpg-wal-s3') {
                    $expectedKeys += [string](Get-Field $target 'secretKey')
                }
            }
        }
        $expectedKeys = @($expectedKeys | Sort-Object)
        $actualKeys = @($secretKeys | Sort-Object)
        if (($expectedKeys -join ',') -eq ($actualKeys -join ',') -and $expectedKeys.Count -gt 0) {
            Add-Check -Step '4b.3' -Name 'cnpg-wal-s3 key set' -Status 'Pass' -Detail "$($actualKeys.Count) key(s): $($actualKeys -join ', ')"
        }
        else {
            Add-Check -Step '4b.3' -Name 'cnpg-wal-s3 key set' -Status 'Fail' -Detail "have [$($actualKeys -join ', ')], parameters.json wants [$($expectedKeys -join ', ')]"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Portability'
    # ---------------------------------------------------------------- #

    # The same check Test-ClusterPlatform.ps1's 3b.13 makes, scoped to this
    # phase's own values: WAL_BUCKET is per-installation the same way DOMAIN
    # and INGRESS_VIP are, and it is new in this phase, so nothing upstream
    # has ever grepped for it. POSTGRES_INSTANCES is excluded for the same
    # reason LONGHORN_REPLICA_COUNT is next door - '2' and '3' are single
    # characters that appear constantly in prose, and grepping for them
    # would be a permanent wall of false positives.
    if ($configData -and (Get-Field $configData 'WAL_BUCKET')) {
        $bucket = [string](Get-Field $configData 'WAL_BUCKET')
        if ($bucket.Length -ge 4) {
            $deployFiles = @(Get-ChildItem -Path $DeployPath -Recurse -File)
            $hits = @($deployFiles | Select-String -SimpleMatch -Pattern $bucket -ErrorAction SilentlyContinue)
            if ($hits.Count -gt 0) {
                $leaks = @($hits | ForEach-Object {
                        $relative = $_.Path.Substring((Resolve-Path $DeployPath).Path.Length).TrimStart('\', '/')
                        "deploy/$relative`:$($_.LineNumber)"
                    })
                Add-Check -Step '4b.11' -Name 'No WAL_BUCKET literal in deploy/' -Status 'Fail' -Detail "$($leaks -join '; '). Belongs as a `${WAL_BUCKET} substitution - see docs/ethos.md"
            }
            else {
                Add-Check -Step '4b.11' -Name 'No WAL_BUCKET literal in deploy/' -Status 'Pass' -Detail "$($deployFiles.Count) file(s) checked"
            }
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
    Select-Object Step, Check, Result, @{ Name = 'Detail'; Expression = { if ($_.Detail.Length -gt 90) { $_.Detail.Substring(0, 89) + '…' } else { $_.Detail } } } |
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
        "## Phase 4 data tier gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { '✅' } 'Warn' { '⚠️' } default { '❌' } }
        $detail = ($check.Detail -replace '\|', '\|')
        $lines += "| $($check.Step) | $($check.Check) | $mark $($check.Result) | $detail |"
    }
    $lines += @(
        ''
        '_The cluster plan, Phase 4b.11. Read-only except for one local psql SELECT inside the primary pod._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 4 data tier gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names the step it belongs to; docs/plans/swarm/phase-4-data-tier.md 4b has the reasoning for each.'
    exit 1
}

Write-Host "Phase 4 data tier gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'This does not replace 4b.1s one-time backup-cycle exit or 4b.9s hand-verified restore -'
Write-Host 'both are proofs a re-run cannot repeat honestly. Re-run this after a node rebuild or a'
Write-Host 'restore; it is read-only (bar one local SELECT) and safe at any time.'
exit 0
