<#
.SYNOPSIS
    Asserts 6b.15's checklist in one run, the same shape
    scripts/k3s/Test-ClusterPlatform.ps1 (3b.13), scripts/k3s/Test-DataTier.ps1
    (4b.11) and scripts/k3s/Test-AppTier.ps1 (5b.14) established. "Phase 6 is
    done" as a command rather than as a memory.

.DESCRIPTION
    Steps 6b.1-6b.14 each did their own work; this is 6b.15 itself - the gate,
    not a re-check of everything those steps already proved.

    Two SSH round trips against -IPAddress, not one. The first (Probe)
    collects every cluster object this script reasons about, plus every HTTP
    call whose path is known up front - the Kubernetes API server's generic
    service proxy (`kubectl get --raw
    /api/.../services/<name>:<port>/proxy/<path>`) reaches Prometheus,
    Alertmanager and OpenSearch without this script needing curl inside a pod
    or a port-forward, the same "read-only, one command" spirit
    Test-AppTier.ps1's api-pod `exec ls` already uses for a different kind of
    dependent read. The second (Series) exists because the six scrape jobs'
    *actual* `job` label - what prometheus-operator assigned a ServiceMonitor,
    PodMonitor or ScrapeConfig, which is a chart/operator-version convention
    this script does not trust itself to predict - is only knowable after
    parsing the first round trip's target list; querying by a guessed job
    name would fail the same way for the right and the wrong reason, and this
    script would rather ask twice than guess once. A third, per-node loop
    (Nodes) checks vm.max_map_count and etcd's :2381, the same shape
    Test-ClusterPlatform.ps1's own Nodes stage established for a check that is
    about each machine rather than about the cluster as a whole.

    Same three properties every phase gate here states and relies on:

    **It does not stop at the first failure.** Nothing here writes anything,
    so continuing past a surprise costs nothing and a table of two dozen
    findings is worth more than the first one.

    **A check it cannot evaluate is a failure, not a skip.** An absent object,
    a connection that never completes, or an unparseable response is "not
    proven", and a gate that reports that as anything but failure can be
    satisfied by a cluster that is off.

    **Expectations come from the cluster and the repository, not from
    parameters.** DOMAIN and INGRESS_VIP come from the live
    aerie-cluster-config ConfigMap; every object name, index name, ISM policy
    id and job identity below is read out of the manifests under
    deploy/cluster/observability/ this phase actually shipped, not typed from
    memory - see each check's own comment for the file it was read from.

    Stages:
      1. Preflight - the SSH key resolves, the client is present, and the
                      node answers 22.
      2. Probe      - one SSH round trip: every cluster object this script
                      reasons about, plus the service-proxied HTTP calls whose
                      path does not depend on anything discovered first.
      3. Nodes      - a second SSH connection per node: vm.max_map_count and
                      etcd's :2381 (6b.1), which are properties of the machine
                      rather than of the cluster's control plane.
      4. Checks     - 6b.15's checklist, evaluated against the Probe/Nodes
                      snapshots.
      5. Series     - a third SSH round trip: the six scrape jobs' series
                      counts, queried by the job label the Probe stage's
                      target list actually reported.
      6. HTTPS      - three direct TLS connections to the VIP, one per
                      hostname - what a browser on the LAN actually sees.
      7. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here over SSH is
    cluster state, the same as Test-DataTier.ps1 and Test-AppTier.ps1.

.EXAMPLE
    .\Test-Observability.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

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
        link is missing. Never used against a label or JSON key that itself
        contains a dot (e.g. cnpg.io/..., OpenSearch's `docs.count`) - those
        go through Get-Field with the literal name instead.
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
        $null when there are none.
    #>
    param([Parameter(Mandatory)][AllowNull()]$List)
    return @(Get-Field $List 'items' | Where-Object { $_ })
}

function Get-Condition {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Type)
    $conditions = @(Get-Path $Object 'status.conditions' | Where-Object { $_ })
    return ($conditions | Where-Object { (Get-Field $_ 'type') -eq $Type } | Select-Object -First 1)
}

function Get-ReadyCondition {
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

function ConvertTo-FlatArray {
    <#
    .SYNOPSIS
        A *top-level* JSON array, as a flat array under both PowerShell
        editions.
    .DESCRIPTION
        Windows PowerShell 5.1 emits a top-level JSON array from
        ConvertFrom-Json as one object rather than enumerating it; PowerShell 6
        changed that and added -NoEnumerate to opt back in. So
        `@($json | ConvertFrom-Json)` is the real array under 7 and a
        one-element array *wrapping* it under 5.1 - and every downstream .Count
        and -join then describes the wrapper instead of the data. The symptom is
        a count of 1 and a literal "System.Object[]" in a check's detail text,
        which is what ../../.github/workflows/verify-observability.yml's
        `shell: powershell` (5.1, not pwsh) produced for the Windows exporter
        target list, the Alertmanager alert list and the OpenSearch index list
        alike.

        Unwrapping one level covers both editions. None of the three arrays this
        is used on has array elements of its own, so the flattening cannot
        collapse real structure; strings are excluded explicitly because a
        string is IEnumerable over its characters.

        Not needed for a JSON array reached as a *property* of an object
        (`data.activeTargets`, `hits.hits`) - that is an ordinary Object[] in
        both editions. Those need `@(...)` at the call site for a different
        reason, which Get-Field's own note covers.
    #>
    param([Parameter(Mandatory)][AllowNull()]$InputObject)
    $items = New-Object Collections.Generic.List[object]
    foreach ($item in @($InputObject)) {
        if ($null -eq $item) { continue }
        if ($item -is [string] -or $item -isnot [System.Collections.IEnumerable]) {
            $items.Add($item)
            continue
        }
        foreach ($inner in $item) { if ($null -ne $inner) { $items.Add($inner) } }
    }
    # Emitted element by element, and every call site wraps the call in
    # `@(...)` - the same contract Get-Items above already uses. Returning
    # `, $items.ToArray()` instead looks safer and is not: `@(<call>)` collects
    # what a function *emits*, so handing it one pre-wrapped array puts the
    # wrapper straight back and this function fixes nothing.
    return $items.ToArray()
}

function ConvertFrom-ProbeJsonArray {
    <#
    .SYNOPSIS
        ConvertFrom-ProbeJson for a section whose JSON is a top-level array.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$Name
    )
    return (ConvertTo-FlatArray (ConvertFrom-ProbeJson -Output $Output -Name $Name))
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
        The object exists and its Ready condition is True. A suspended object
        is checked before its condition rather than after - a suspended
        object keeps whatever condition it last had, which is history, not
        current state.
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

function Get-CertificateDnsNames {
    <#
    .SYNOPSIS
        The DNS names out of a certificate's subjectAltName extension. Copy
        of Test-ClusterPlatform.ps1's own - see that file for why this parses
        the extension's DER by hand rather than trusting X509Extension's
        locale- and runtime-dependent Format().
    #>
    param([Parameter(Mandatory)]$Certificate)
    $names = New-Object Collections.Generic.List[string]

    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne '2.5.29.17') { continue }

        $bytes = $extension.RawData
        if ($null -eq $bytes -or $bytes.Length -lt 2 -or $bytes[0] -ne 0x30) { continue }

        $index = 1
        if ($bytes[$index] -band 0x80) { $index += 1 + ($bytes[$index] -band 0x7F) } else { $index += 1 }

        while ($index -lt $bytes.Length - 1) {
            $tag = $bytes[$index]; $index++
            $length = $bytes[$index]; $index++
            if ($length -band 0x80) {
                $count = $length -band 0x7F
                $length = 0
                for ($step = 0; $step -lt $count -and $index -lt $bytes.Length; $step++) {
                    $length = ($length * 256) + $bytes[$index]; $index++
                }
            }
            if ($index + $length -gt $bytes.Length) { break }
            if ($tag -eq 0x82 -and $length -gt 0) {
                $names.Add([Text.Encoding]::ASCII.GetString($bytes, $index, $length))
            }
            $index += $length
        }
    }

    if ($names.Count -eq 0) {
        foreach ($extension in $Certificate.Extensions) {
            if ($extension.Oid.Value -ne '2.5.29.17') { continue }
            foreach ($part in ($extension.Format($true) -split "[,\r\n]")) {
                $separator = $part.IndexOf('=')
                if ($separator -lt 0) { continue }
                $value = $part.Substring($separator + 1).Trim()
                if ($value) { $names.Add($value) }
            }
        }
    }
    return @($names)
}

function Invoke-HttpsGet {
    <#
    .SYNOPSIS
        Opens one TLS connection to an address, sends one GET, and returns
        the status code and the certificate the server presented. Trimmed
        copy of Test-AppTier.ps1's own - this script never needs the response
        body, only the certificate every hostname serves.
    #>
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$ServerName,
        [string]$Path = '/',
        [int]$Port = 443,
        [int]$TimeoutMs = 15000
    )

    $result = [pscustomobject]@{
        Connected   = $false
        StatusCode  = $null
        Certificate = $null
        Error       = $null
    }

    $client = $null
    $ssl = $null
    try {
        $client = New-Object Net.Sockets.TcpClient
        $connect = $client.BeginConnect($IPAddress, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            $result.Error = "no answer on ${IPAddress}:$Port within $([int]($TimeoutMs / 1000))s"
            return $result
        }
        $client.EndConnect($connect)

        $accept = [Net.Security.RemoteCertificateValidationCallback] { param($channel, $peerCertificate, $chain, $policyErrors) return $true }
        $ssl = New-Object Net.Security.SslStream($client.GetStream(), $false, $accept)
        $ssl.ReadTimeout = $TimeoutMs
        $ssl.WriteTimeout = $TimeoutMs

        $protocols = [Security.Authentication.SslProtocols]::Tls12
        try { $protocols = $protocols -bor [Security.Authentication.SslProtocols]::Tls13 } catch { }
        $ssl.AuthenticateAsClient($ServerName, $null, $protocols, $false)

        if ($ssl.RemoteCertificate) {
            $result.Certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        }

        $writer = New-Object IO.StreamWriter($ssl, (New-Object Text.ASCIIEncoding))
        $writer.NewLine = "`r`n"
        $writer.AutoFlush = $true
        $writer.WriteLine("GET $Path HTTP/1.1")
        $writer.WriteLine("Host: $ServerName")
        $writer.WriteLine('User-Agent: aerie-test-observability')
        $writer.WriteLine('Connection: close')
        $writer.WriteLine()

        $reader = New-Object IO.StreamReader($ssl, [Text.Encoding]::ASCII)
        $statusLine = $reader.ReadLine()
        $result.Connected = $true
        if ($statusLine -match '^HTTP/\d\.\d\s+(\d{3})') { $result.StatusCode = [int]$Matches[1] }
    }
    catch {
        $result.Error = $_.Exception.Message
    }
    finally {
        if ($ssl) { $ssl.Dispose() }
        if ($client) { $client.Close() }
    }

    return $result
}

function Test-ProductionCertificate {
    <#
    .SYNOPSIS
        True when a certificate is the live wildcard 3b.10 bought - not
        staging, not expired, and covering the given host - with the reason
        it isn't appended to $Reason when it fails.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$Certificate,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][string]$HostName,
        [ref]$Reason
    )
    if ($null -eq $Certificate) { $Reason.Value = 'the handshake completed without a peer certificate'; return $false }
    $issuer = $Certificate.Issuer
    $names = Get-CertificateDnsNames $Certificate
    $problems = New-Object Collections.Generic.List[string]
    if ($issuer -match 'STAGING') {
        $problems.Add("issued by the Let's Encrypt *staging* hierarchy ($issuer)")
    }
    elseif ($issuer -notmatch "Let's Encrypt") {
        $problems.Add("issued by '$issuer' - if that is TRAEFIK DEFAULT CERT, the TLSStore is not serving the wildcard")
    }
    if ($names -notcontains "*.$Domain" -and $names -notcontains $HostName) {
        $problems.Add("subjectAltName does not cover $HostName (has: $($names -join ', '))")
    }
    if ($Certificate.NotAfter -lt (Get-Date)) { $problems.Add("expired on $($Certificate.NotAfter.ToString('yyyy-MM-dd'))") }
    if ($problems.Count -gt 0) { $Reason.Value = ($problems -join '; '); return $false }
    return $true
}

function ConvertTo-PromQlRawPath {
    <#
    .SYNOPSIS
        Builds the `kubectl get --raw` path for one PromQL instant query,
        proxied through the Kubernetes API server's generic service proxy -
        see this script's own header comment for why that mechanism is used
        instead of curl inside a pod.
    #>
    param([Parameter(Mandatory)][string]$Query)
    $encoded = [Uri]::EscapeDataString($Query)
    return "/api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/query?query=$encoded"
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-observability-gate'
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

    Write-Host "Cluster:  $IPAddress"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host 'Preflight OK. Everything below is read-only: `kubectl get`, `kubectl get --raw` GETs proxied to in-cluster services, and TLS handshakes against the VIP.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-observability-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip for everything whose path doesn't depend on anything
    # discovered first. Every command falls back to `echo {}` / `echo []` on
    # an unregistered type, a missing object or a proxied 4xx/5xx, single
    # quotes only - Invoke-NodeSsh refuses a command containing a double
    # quote (Windows PowerShell 5.1 would otherwise strip it en route).
    #
    # The `kubectl get --raw /api/v1/namespaces/<ns>/services/<name:port>/proxy/<path>`
    # calls below reach Prometheus, Alertmanager and OpenSearch through the
    # Kubernetes API server's own generic service proxy - no curl inside a
    # pod, no port-forward, and the same admin credential every other command
    # here already runs as. `kubectl get --raw` exits non-zero on anything
    # but a 2xx from the proxied service, which is exactly the existence
    # signal the ISM-policy/index-template/index-pattern checks want.
    $probeScript = @(
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- nodes\n'''
        'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
        'printf ''\n--- kustomizationcontrollers\n'''
        'sudo k3s kubectl -n flux-system get kustomizations.kustomize.toolkit.fluxcd.io observability-controllers -o json 2>/dev/null || echo {}'
        'printf ''\n--- kustomizationconfig\n'''
        'sudo k3s kubectl -n flux-system get kustomizations.kustomize.toolkit.fluxcd.io observability-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- externalsecrets\n'''
        'sudo k3s kubectl -n observability get externalsecrets.external-secrets.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- kumaadminsecret\n'''
        'sudo k3s kubectl -n observability get secret kuma-admin -o jsonpath={.metadata.name} 2>/dev/null || true'
        'printf ''\n--- grafanaadminsecret\n'''
        'sudo k3s kubectl -n observability get secret grafana-admin -o jsonpath={.metadata.name} 2>/dev/null || true'
        'printf ''\n--- pods\n'''
        'sudo k3s kubectl -n observability get pods -o json 2>/dev/null || echo {}'
        'printf ''\n--- priorityclass\n'''
        'sudo k3s kubectl get priorityclasses.scheduling.k8s.io aerie-observability -o json 2>/dev/null || echo {}'
        'printf ''\n--- promtargets\n'''
        'sudo k3s kubectl get --raw /api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/targets 2>/dev/null || echo {}'
        'printf ''\n--- promrules\n'''
        'sudo k3s kubectl get --raw /api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/rules 2>/dev/null || echo {}'
        'printf ''\n--- amalerts\n'''
        'sudo k3s kubectl get --raw /api/v1/namespaces/observability/services/alertmanager-operated:9093/proxy/api/v2/alerts 2>/dev/null || echo []'
        'printf ''\n--- oshealth\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-cluster-master:9200/proxy/_cluster/health' 2>/dev/null || echo {}"
        'printf ''\n--- osindices\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-cluster-master:9200/proxy/_cat/indices/aerie-logs-*?format=json' 2>/dev/null || echo []"
        'printf ''\n--- oslogsample\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-cluster-master:9200/proxy/aerie-logs-*/_search?size=200&_source=service,kubernetes.container_name' 2>/dev/null || echo {}"
        'printf ''\n--- osismpolicy\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-cluster-master:9200/proxy/_plugins/_ism/policies/aerie-log-retention' >/dev/null 2>&1 && echo yes || echo no"
        'printf ''\n--- osindextemplate\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-cluster-master:9200/proxy/_index_template/aerie-logs' >/dev/null 2>&1 && echo yes || echo no"
        'printf ''\n--- osindexpattern\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/opensearch-dashboards:5601/proxy/api/saved_objects/index-pattern/aerie-logs' >/dev/null 2>&1 && echo yes || echo no"
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
    $nodes = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'nodes'))
    $kustomizationControllers = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'kustomizationcontrollers'
    $kustomizationConfig = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'kustomizationconfig'
    $externalSecrets = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'externalsecrets'))
    $kumaAdminSecretName = (Get-ProbeSection -Output $probe.StdOut -Name 'kumaadminsecret').Trim()
    $grafanaAdminSecretName = (Get-ProbeSection -Output $probe.StdOut -Name 'grafanaadminsecret').Trim()
    $pods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pods'))
    $priorityClass = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'priorityclass'
    $promTargets = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'promtargets'
    $promRules = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'promrules'
    $amAlerts = @(ConvertFrom-ProbeJsonArray -Output $probe.StdOut -Name 'amalerts')
    $osHealth = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'oshealth'
    $osIndices = @(ConvertFrom-ProbeJsonArray -Output $probe.StdOut -Name 'osindices')
    $osLogSample = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'oslogsample'
    $osIsmPolicyExists = (Get-ProbeSection -Output $probe.StdOut -Name 'osismpolicy').Trim() -eq 'yes'
    $osIndexTemplateExists = (Get-ProbeSection -Output $probe.StdOut -Name 'osindextemplate').Trim() -eq 'yes'
    $osIndexPatternExists = (Get-ProbeSection -Output $probe.StdOut -Name 'osindexpattern').Trim() -eq 'yes'

    $configData = Get-Field $clusterConfig 'data'
    $configValues = @{}
    if ($configData) { foreach ($property in $configData.PSObject.Properties) { $configValues[$property.Name] = [string]$property.Value } }
    $domain = if ($configValues.ContainsKey('DOMAIN')) { $configValues['DOMAIN'] } else { $null }
    $ingressVip = if ($configValues.ContainsKey('INGRESS_VIP')) { $configValues['INGRESS_VIP'] } else { $null }
    $windowsExporterTargetsRaw = if ($configValues.ContainsKey('WINDOWS_EXPORTER_TARGETS')) { $configValues['WINDOWS_EXPORTER_TARGETS'] } else { $null }

    $activeTargets = @(Get-Path $promTargets 'data.activeTargets' | Where-Object { $_ })

    # ---------------------------------------------------------------- #
    Write-Stage 'Nodes'
    # ---------------------------------------------------------------- #

    # 6b.1: vm.max_map_count and etcd's :2381 are properties of each machine,
    # not of the cluster's control plane - Test-ClusterPlatform.ps1's own
    # Nodes stage is the precedent for checking "every node" as its own set
    # of SSH connections rather than folding it into the one-round-trip Probe
    # above, which only ever talks to the node named by -IPAddress.
    $nodeAddresses = @()
    foreach ($node in $nodes) {
        $address = @(Get-Path $node 'status.addresses' | Where-Object { (Get-Field $_ 'type') -eq 'InternalIP' } | Select-Object -First 1)
        if ($address.Count -gt 0) {
            $nodeAddresses += [pscustomobject]@{
                Name    = [string](Get-Path $node 'metadata.name')
                Address = [string](Get-Field $address[0] 'address')
            }
        }
    }

    if ($nodeAddresses.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'vm.max_map_count and etcd :2381 on every node' -Status 'Fail' -Detail 'no node reported an InternalIP, so no node could be reached'
    }

    foreach ($node in $nodeAddresses) {
        $label = "$($node.Name) ($($node.Address))"

        if (-not (Test-TcpPort -IPAddress $node.Address -Port 22)) {
            Add-Check -Step '6b.15' -Name "vm.max_map_count and etcd :2381 on $($node.Name)" -Status 'Fail' -Detail "$($node.Address) is not answering on port 22 from here"
            continue
        }

        $nodeSsh = @{ IPAddress = $node.Address; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }
        $nodeProbe = Invoke-NodeSsh @nodeSsh -ConnectTimeoutSec 20 -Command (@(
                'echo ''--- maxmap'''
                'sudo sysctl -n vm.max_map_count 2>/dev/null || echo 0'
                'echo ''--- etcdmetrics'''
                # -w's trailing \n is load-bearing: without it curl writes the status
                # code with no line terminator, it runs into the next
                # `printf '--- ...'` marker, and Get-ProbeSection hands back
                # "200--- end" - a passing node reported as a failing one.
                'curl -s --max-time 5 -o /dev/null -w ''%{http_code}\n'' http://127.0.0.1:2381/metrics 2>/dev/null || echo 000'
                'echo ''--- end'''
            ) -join '; ')

        if ($nodeProbe.ExitCode -ne 0) {
            Add-Check -Step '6b.15' -Name "vm.max_map_count and etcd :2381 on $($node.Name)" -Status 'Fail' -Detail "SSH to $label failed: $(($nodeProbe.StdErr -replace '\s+', ' ').Trim())"
            continue
        }

        $maxMap = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'maxmap').Trim()
        if ($maxMap -eq '262144') {
            Add-Check -Step '6b.15' -Name "vm.max_map_count on $($node.Name)" -Status 'Pass' -Detail '262144'
        }
        else {
            Add-Check -Step '6b.15' -Name "vm.max_map_count on $($node.Name)" -Status 'Fail' -Detail "reads '$maxMap', not 262144 - OpenSearch (6b.9) refuses to start with this unset"
        }

        $etcdStatus = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'etcdmetrics').Trim()
        if ($etcdStatus -eq '200') {
            Add-Check -Step '6b.15' -Name "etcd metrics endpoint (:2381) on $($node.Name)" -Status 'Pass' -Detail '200'
        }
        else {
            Add-Check -Step '6b.15' -Name "etcd metrics endpoint (:2381) on $($node.Name)" -Status 'Fail' -Detail "HTTP $etcdStatus - confirm etcd-expose-metrics: true (scripts/k3s/Install-K3sNode.ps1, 6b.1) and that k3s restarted after it was set"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- 6b.2: every ExternalSecret in observability reports SecretSynced -
    foreach ($name in @('ghcr-pull', 'home-assistant')) {
        $external = $externalSecrets | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        if ($null -eq $external) {
            Add-Check -Step '6b.15' -Name "ExternalSecret $name (6b.2)" -Status 'Fail' -Detail 'not found in namespace observability'
            continue
        }
        $condition = Get-ReadyCondition $external
        if ((Get-Field $condition 'status') -eq 'True' -and (Get-Field $condition 'reason') -eq 'SecretSynced') {
            Add-Check -Step '6b.15' -Name "ExternalSecret $name (6b.2)" -Status 'Pass' -Detail 'SecretSynced'
        }
        else {
            Add-Check -Step '6b.15' -Name "ExternalSecret $name (6b.2)" -Status 'Fail' -Detail (Format-Condition $condition)
        }
    }

    # --- 6b.3, 6a.2: WINDOWS_EXPORTER_TARGETS parses, and every host in it -
    # answers from inside a pod - proven by Prometheus's own scrape of it
    # (deploy/cluster/observability/config/scrape/windows-exporter.yaml),
    # which runs inside a pod on the cluster network by construction, rather
    # than by this script execing into one to curl the same thing again.
    $windowsExporterHosts = $null
    if (-not $windowsExporterTargetsRaw) {
        Add-Check -Step '6b.15' -Name 'WINDOWS_EXPORTER_TARGETS parses (6b.3)' -Status 'Fail' -Detail 'not set in aerie-cluster-config'
    }
    else {
        try {
            $parsed = @(ConvertTo-FlatArray ($windowsExporterTargetsRaw | ConvertFrom-Json))
            $badEntries = @($parsed | Where-Object { $_ -notmatch '^[^:\s]+:\d+$' })
            if ($parsed.Count -eq 0) {
                Add-Check -Step '6b.15' -Name 'WINDOWS_EXPORTER_TARGETS parses (6b.3)' -Status 'Fail' -Detail 'parsed to an empty list'
            }
            elseif ($badEntries.Count -gt 0) {
                Add-Check -Step '6b.15' -Name 'WINDOWS_EXPORTER_TARGETS parses (6b.3)' -Status 'Fail' -Detail "not host:port - $($badEntries -join ', ')"
            }
            else {
                $windowsExporterHosts = $parsed
                Add-Check -Step '6b.15' -Name 'WINDOWS_EXPORTER_TARGETS parses (6b.3)' -Status 'Pass' -Detail "$($parsed.Count) host(s): $($parsed -join ', ')"
            }
        }
        catch {
            Add-Check -Step '6b.15' -Name 'WINDOWS_EXPORTER_TARGETS parses (6b.3)' -Status 'Fail' -Detail "not valid JSON: $($_.Exception.Message)"
        }
    }

    $windowsExporterTargets = @($activeTargets | Where-Object { [string](Get-Field $_ 'scrapePool') -like '*observability/windows-exporter*' })
    if (-not $windowsExporterHosts) {
        Add-Check -Step '6b.15' -Name 'Every Windows host answers from inside a pod (6b.3, 6a.2)' -Status 'Fail' -Detail 'WINDOWS_EXPORTER_TARGETS did not parse - see the check above'
    }
    elseif ($windowsExporterTargets.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'Every Windows host answers from inside a pod (6b.3, 6a.2)' -Status 'Fail' -Detail 'no active target in scrapePool *observability/windows-exporter* - the ScrapeConfig object (6b.6) is missing or Prometheus has not discovered it yet'
    }
    else {
        $observedAddresses = @($windowsExporterTargets | ForEach-Object { [string](Get-Path $_ 'labels.instance') } | Where-Object { $_ })
        $missing = @($windowsExporterHosts | Where-Object { $observedAddresses -notcontains $_ })
        $down = @($windowsExporterTargets | Where-Object { (Get-Field $_ 'health') -ne 'up' } | ForEach-Object { "$(Get-Path $_ 'labels.instance'): $(Get-Field $_ 'lastError')" })
        if ($missing.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Every Windows host answers from inside a pod (6b.3, 6a.2)' -Status 'Fail' -Detail "no target for: $($missing -join ', ') - present: $($observedAddresses -join ', ')"
        }
        elseif ($down.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Every Windows host answers from inside a pod (6b.3, 6a.2)' -Status 'Fail' -Detail ($down -join '; ')
        }
        else {
            Add-Check -Step '6b.15' -Name 'Every Windows host answers from inside a pod (6b.3, 6a.2)' -Status 'Pass' -Detail "$($observedAddresses.Count) host(s) up: $($observedAddresses -join ', ')"
        }
    }

    # --- 6b.4: both Kustomizations Ready; kuma-admin/grafana-admin present -
    # existence only, never the value - an operator who has changed either
    # password from the UI must not fail this gate.
    [void](Add-ObjectReadyCheck -Step '6b.15' -Name 'Kustomization observability-controllers (6b.4)' -Object $kustomizationControllers)
    [void](Add-ObjectReadyCheck -Step '6b.15' -Name 'Kustomization observability-config (6b.4)' -Object $kustomizationConfig)
    if ($kumaAdminSecretName -eq 'kuma-admin') {
        Add-Check -Step '6b.15' -Name 'Secret kuma-admin exists (6b.4)' -Status 'Pass' -Detail 'present'
    }
    else {
        Add-Check -Step '6b.15' -Name 'Secret kuma-admin exists (6b.4)' -Status 'Fail' -Detail 'not found in namespace observability'
    }
    if ($grafanaAdminSecretName -eq 'grafana-admin') {
        Add-Check -Step '6b.15' -Name 'Secret grafana-admin exists (6b.4)' -Status 'Pass' -Detail 'present'
    }
    else {
        Add-Check -Step '6b.15' -Name 'Secret grafana-admin exists (6b.4)' -Status 'Fail' -Detail 'not found in namespace observability'
    }

    # --- Prometheus reports zero down targets - covers 6b.1, 6b.5, 6b.6 ----
    if ($activeTargets.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'Prometheus: zero down targets (6b.1, 6b.5, 6b.6)' -Status 'Fail' -Detail 'no active targets reported at all - Prometheus is not reachable through the service proxy, or has discovered nothing'
    }
    else {
        $downTargets = @($activeTargets | Where-Object { (Get-Field $_ 'health') -ne 'up' } | ForEach-Object {
                $pool = Get-Field $_ 'scrapePool'
                $instance = Get-Path $_ 'labels.instance'
                "$pool ($instance): $(Get-Field $_ 'lastError')"
            })
        if ($downTargets.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Prometheus: zero down targets (6b.1, 6b.5, 6b.6)' -Status 'Fail' -Detail ($downTargets -join '; ')
        }
        else {
            Add-Check -Step '6b.15' -Name 'Prometheus: zero down targets (6b.1, 6b.5, 6b.6)' -Status 'Pass' -Detail "$($activeTargets.Count) target(s), all up"
        }
    }

    # --- Identify the six scrape jobs' actual `job` label, from what -------
    # Prometheus itself reports rather than a guessed naming convention -
    # see this script's own header comment. Each entry's object/file is the
    # thing 6b.6 (or, for etcd, 6b.5's kubeEtcd block) actually shipped.
    $scrapeJobSources = @(
        [pscustomobject]@{ Label = 'windows-exporter (ScrapeConfig, config/scrape/windows-exporter.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*observability/windows-exporter*' } }
        [pscustomobject]@{ Label = 'flux-system (PodMonitor, config/scrape/flux.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*observability/flux-system*' } }
        [pscustomobject]@{ Label = 'cloudnative-pg (PodMonitor, config/scrape/cloudnative-pg.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*observability/cloudnative-pg*' } }
        [pscustomobject]@{ Label = 'longhorn (ServiceMonitor, config/scrape/longhorn.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*observability/longhorn*' } }
        [pscustomobject]@{ Label = 'traefik (ServiceMonitor, config/scrape/traefik.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*observability/traefik*' } }
        # kubeEtcd (kube-prometheus-stack.yaml) - a chart-rendered Endpoints
        # object, not one of this phase's own hand-written scrape objects, so
        # matched by the job label config/alerts/cluster.yaml's own
        # EtcdMemberDown rule already hardcodes rather than by scrapePool.
        # Matched on scrapePool, not on `job`: the chart's kube-etcd
        # ServiceMonitor sets jobLabel: jobLabel, and the Service it selects
        # carries jobLabel: kube-etcd - so the `job` label on these targets is
        # `kube-etcd`, not the release-prefixed name every other exporter in
        # the chart uses. Expecting the latter failed a working scrape of both
        # etcd members. scrapePool is derived from the ServiceMonitor's own
        # namespace and name, so it says what this check actually means.
        [pscustomobject]@{ Label = 'kube-prometheus-stack-kube-etcd (kubeEtcd, controllers/kube-prometheus-stack.yaml)'; Match = { param($t) [string](Get-Field $t 'scrapePool') -like '*kube-prometheus-stack-kube-etcd*' } }
    )

    $resolvedJobs = New-Object Collections.Generic.List[psobject]
    foreach ($source in $scrapeJobSources) {
        $matchingTargets = @($activeTargets | Where-Object { & $source.Match $_ })
        if ($matchingTargets.Count -eq 0) {
            Add-Check -Step '6b.15' -Name "Scrape job present: $($source.Label) (6b.6)" -Status 'Fail' -Detail 'no active target found'
            continue
        }
        $job = [string](Get-Path $matchingTargets[0] 'labels.job')
        if (-not $job) {
            Add-Check -Step '6b.15' -Name "Scrape job present: $($source.Label) (6b.6)" -Status 'Fail' -Detail "target found but carries no job label: $($matchingTargets | ConvertTo-Json -Compress -Depth 4)"
            continue
        }
        Add-Check -Step '6b.15' -Name "Scrape job present: $($source.Label) (6b.6)" -Status 'Pass' -Detail "job=$job, $($matchingTargets.Count) target(s)"
        $resolvedJobs.Add([pscustomobject]@{ Label = $source.Label; Job = $job })
    }

    # --- both PrometheusRules loaded with no evaluation errors (6b.8) ------
    $ruleGroups = @(Get-Path $promRules 'data.groups' | Where-Object { $_ })
    if ($ruleGroups.Count -eq 0 -and (Get-Field $promRules 'status') -ne 'success') {
        Add-Check -Step '6b.15' -Name 'PrometheusRule cluster-alerts loaded (6b.8)' -Status 'Fail' -Detail 'could not read /api/v1/rules from Prometheus through the service proxy'
        Add-Check -Step '6b.15' -Name 'PrometheusRule flux-alerts loaded (6b.8)' -Status 'Fail' -Detail 'could not read /api/v1/rules from Prometheus through the service proxy'
    }
    else {
        foreach ($ruleFile in @('cluster-alerts', 'flux-alerts')) {
            $matchingGroups = @($ruleGroups | Where-Object { [string](Get-Field $_ 'file') -like "*$ruleFile*" })
            if ($matchingGroups.Count -eq 0) {
                Add-Check -Step '6b.15' -Name "PrometheusRule $ruleFile loaded (6b.8)" -Status 'Fail' -Detail 'no rule group whose file matches this object name - not loaded, or config/alerts/kustomization.yaml dropped it'
                continue
            }
            $allRules = @($matchingGroups | ForEach-Object { Get-Field $_ 'rules' } | Where-Object { $_ })
            $erroring = @($allRules | Where-Object { [string](Get-Field $_ 'lastError') -or [string](Get-Field $_ 'health') -eq 'err' } | ForEach-Object { "$(Get-Field $_ 'name'): $(Get-Field $_ 'lastError')" })
            if ($allRules.Count -eq 0) {
                Add-Check -Step '6b.15' -Name "PrometheusRule $ruleFile loaded (6b.8)" -Status 'Fail' -Detail 'matched a file but carries zero rules'
            }
            elseif ($erroring.Count -gt 0) {
                Add-Check -Step '6b.15' -Name "PrometheusRule $ruleFile loaded (6b.8)" -Status 'Fail' -Detail ($erroring -join '; ')
            }
            else {
                Add-Check -Step '6b.15' -Name "PrometheusRule $ruleFile loaded (6b.8)" -Status 'Pass' -Detail "$($allRules.Count) rule(s), no evaluation errors"
            }
        }
    }

    # --- Alertmanager shows the Watchdog alert firing to a receiver (6b.8) -
    $watchdog = $amAlerts | Where-Object { (Get-Path $_ 'labels.alertname') -eq 'Watchdog' } | Select-Object -First 1
    if ($null -eq $watchdog) {
        Add-Check -Step '6b.15' -Name 'Alertmanager: Watchdog firing to a receiver (6b.8)' -Status 'Fail' -Detail 'no Watchdog alert in /api/v2/alerts - Prometheus, this rule''s evaluation, or Alertmanager itself is down'
    }
    else {
        $state = [string](Get-Path $watchdog 'status.state')
        $receivers = @(Get-Field $watchdog 'receivers' | ForEach-Object { Get-Field $_ 'name' } | Where-Object { $_ })
        if ($state -eq 'active' -and $receivers.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Alertmanager: Watchdog firing to a receiver (6b.8)' -Status 'Pass' -Detail "state=active, receiver(s): $($receivers -join ', ')"
        }
        else {
            Add-Check -Step '6b.15' -Name 'Alertmanager: Watchdog firing to a receiver (6b.8)' -Status 'Fail' -Detail "state=$state, receiver(s): $($receivers -join ', ')"
        }
    }

    # --- OpenSearch cluster health is green, not yellow (6b.9) -------------
    $osStatus = [string](Get-Field $osHealth 'status')
    if ($osStatus -eq 'green') {
        Add-Check -Step '6b.15' -Name 'OpenSearch cluster health (6b.9)' -Status 'Pass' -Detail 'green'
    }
    elseif ($osStatus) {
        Add-Check -Step '6b.15' -Name 'OpenSearch cluster health (6b.9)' -Status 'Fail' -Detail "$osStatus, not green - if yellow, ../config/provisioning/apply-index-template.sh's number_of_replicas: 0 (for singleNode) did not apply to every index"
    }
    else {
        Add-Check -Step '6b.15' -Name 'OpenSearch cluster health (6b.9)' -Status 'Fail' -Detail 'could not read _cluster/health through the service proxy'
    }

    # --- aerie-logs-* doc count, and both halves of the Lua produce results
    # (6b.10). "Climbing" cannot be proven from one snapshot; this checks the
    # proxy this script actually can observe in one run - documents present
    # now, and (from a sample of them) evidence both the default branch
    # (service == kubernetes.container_name) and the override branch
    # (service overwritten by State.Service, so the two differ) have fired -
    # see controllers/fluent-bit/service_tag.lua's own precedence comment.
    $totalDocs = 0
    foreach ($index in $osIndices) { $count = 0; [void][int]::TryParse([string](Get-Field $index 'docs.count'), [ref]$count); $totalDocs += $count }
    if ($osIndices.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'aerie-logs-* has documents (6b.10)' -Status 'Fail' -Detail 'no aerie-logs-* index exists yet - fluent-bit has shipped nothing'
    }
    elseif ($totalDocs -eq 0) {
        Add-Check -Step '6b.15' -Name 'aerie-logs-* has documents (6b.10)' -Status 'Fail' -Detail "$($osIndices.Count) index(es), 0 documents total"
    }
    else {
        Add-Check -Step '6b.15' -Name 'aerie-logs-* has documents (6b.10)' -Status 'Pass' -Detail "$totalDocs document(s) across $($osIndices.Count) index(es)"
    }

    $logHits = @(Get-Path $osLogSample 'hits.hits' | Where-Object { $_ })
    if ($logHits.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'Both halves of the service_tag.lua fire (6b.10)' -Status 'Fail' -Detail 'no documents sampled from aerie-logs-* to check'
    }
    else {
        $defaultBranchSeen = $false
        $overrideBranchSeen = $false
        foreach ($hit in $logHits) {
            $source = Get-Field $hit '_source'
            $service = [string](Get-Field $source 'service')
            $containerName = [string](Get-Path $source 'kubernetes.container_name')
            if (-not $service) { continue }
            if ($containerName -and $service -eq $containerName) { $defaultBranchSeen = $true }
            elseif ($containerName -and $service -ne $containerName) { $overrideBranchSeen = $true }
        }
        if ($defaultBranchSeen -and $overrideBranchSeen) {
            Add-Check -Step '6b.15' -Name 'Both halves of the service_tag.lua fire (6b.10)' -Status 'Pass' -Detail "sampled $($logHits.Count) doc(s): both service==kubernetes.container_name and an overridden value seen"
        }
        elseif ($defaultBranchSeen) {
            Add-Check -Step '6b.15' -Name 'Both halves of the service_tag.lua fire (6b.10)' -Status 'Warn' -Detail "sampled $($logHits.Count) doc(s): only the default branch (service==kubernetes.container_name) seen - Aerie.Api's UiLogsController/VmConsoleLogsController State.Service override has not appeared in this sample yet"
        }
        else {
            Add-Check -Step '6b.15' -Name 'Both halves of the service_tag.lua fire (6b.10)' -Status 'Fail' -Detail "sampled $($logHits.Count) doc(s): neither branch confirmed - service_tag.lua may not be wired into the pipeline (controllers/fluent-bit.yaml's extraVolumeMounts)"
        }
    }

    # --- the ISM policy, the index template and the index pattern (6b.11) -
    if ($osIsmPolicyExists) { Add-Check -Step '6b.15' -Name 'ISM policy aerie-log-retention exists (6b.11)' -Status 'Pass' -Detail 'present' }
    else { Add-Check -Step '6b.15' -Name 'ISM policy aerie-log-retention exists (6b.11)' -Status 'Fail' -Detail 'not found - config/provisioning/apply-ism-policy.sh has not run successfully' }
    if ($osIndexTemplateExists) { Add-Check -Step '6b.15' -Name 'Index template aerie-logs exists (6b.11)' -Status 'Pass' -Detail 'present' }
    else { Add-Check -Step '6b.15' -Name 'Index template aerie-logs exists (6b.11)' -Status 'Fail' -Detail 'not found - config/provisioning/apply-index-template.sh has not run successfully' }
    if ($osIndexPatternExists) { Add-Check -Step '6b.15' -Name 'Index pattern aerie-logs exists (6b.11)' -Status 'Pass' -Detail 'present' }
    else { Add-Check -Step '6b.15' -Name 'Index pattern aerie-logs exists (6b.11)' -Status 'Fail' -Detail 'not found - config/provisioning/create-index-pattern.sh has not run successfully (it waits for a "time" field to be discoverable, so this can lag the other two by up to an hour on a fresh install)' }

    # --- every container in observability has requests, every pod carries -
    # the PriorityClass (6b.14). The resource sweep also covers whatever
    # CronJob pod (6b.11, 6b.13) happens to still exist at probe time -
    # ttlSecondsAfterFinished on both keeps one around for up to an hour
    # after it finishes, so this is opportunistic rather than guaranteed to
    # ever see one, exactly as 6b.14's own text expects.
    $priorityClassValue = Get-Field $priorityClass 'value'
    # `globalDefault` is `omitempty` on the API's PriorityClass type, so the
    # default of false is absent from the JSON rather than present as
    # `false` - an absent field and an explicit false are the same state, and
    # only the true case is a finding. Comparing the raw value against $false
    # fails a correct PriorityClass, which is what ../../deploy/cluster/
    # observability/controllers/priorityclass.yaml ships.
    $priorityClassGlobalDefault = [bool](Get-Field $priorityClass 'globalDefault')
    if ($null -eq $priorityClass -or -not (Get-Path $priorityClass 'metadata.name')) {
        Add-Check -Step '6b.15' -Name 'PriorityClass aerie-observability exists (6b.14)' -Status 'Fail' -Detail 'not found'
    }
    elseif ($priorityClassValue -eq -10 -and $priorityClassGlobalDefault -eq $false) {
        Add-Check -Step '6b.15' -Name 'PriorityClass aerie-observability exists (6b.14)' -Status 'Pass' -Detail 'value=-10, globalDefault=false'
    }
    else {
        Add-Check -Step '6b.15' -Name 'PriorityClass aerie-observability exists (6b.14)' -Status 'Fail' -Detail "value=$priorityClassValue, globalDefault=$priorityClassGlobalDefault - want value=-10, globalDefault=false"
    }

    if ($pods.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'Every container declares resources.requests (6b.14)' -Status 'Fail' -Detail 'no pods found in namespace observability'
        Add-Check -Step '6b.15' -Name 'Every pod carries priorityClassName aerie-observability (6b.14)' -Status 'Fail' -Detail 'no pods found in namespace observability'
    }
    else {
        $undeclared = @($pods | ForEach-Object {
                $podName = Get-Path $_ 'metadata.name'
                $containers = @(Get-Path $_ 'spec.containers' | Where-Object { $_ })
                foreach ($container in $containers) {
                    $requests = Get-Path $container 'resources.requests'
                    $cpu = Get-Field $requests 'cpu'
                    $memory = Get-Field $requests 'memory'
                    if (-not $cpu -or -not $memory) { "$podName/$(Get-Field $container 'name')" }
                }
            } | Where-Object { $_ })
        if ($undeclared.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Every container declares resources.requests (6b.14)' -Status 'Fail' -Detail "missing cpu and/or memory requests: $($undeclared -join ', ')"
        }
        else {
            $containerCount = ($pods | ForEach-Object { @(Get-Path $_ 'spec.containers' | Where-Object { $_ }).Count } | Measure-Object -Sum).Sum
            Add-Check -Step '6b.15' -Name 'Every container declares resources.requests (6b.14)' -Status 'Pass' -Detail "$containerCount container(s) across $($pods.Count) pod(s)"
        }

        $wrongPriority = @($pods | Where-Object { (Get-Path $_ 'spec.priorityClassName') -ne 'aerie-observability' } | ForEach-Object { "$(Get-Path $_ 'metadata.name'): '$(Get-Path $_ 'spec.priorityClassName')'" })
        if ($wrongPriority.Count -gt 0) {
            Add-Check -Step '6b.15' -Name 'Every pod carries priorityClassName aerie-observability (6b.14)' -Status 'Fail' -Detail ($wrongPriority -join '; ')
        }
        else {
            Add-Check -Step '6b.15' -Name 'Every pod carries priorityClassName aerie-observability (6b.14)' -Status 'Pass' -Detail "$($pods.Count) pod(s)"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Series'
    # ---------------------------------------------------------------- #

    # The six scrape jobs' series counts, queried by the job label the Probe
    # stage's target list actually reported - see this script's own header
    # comment for why this needs a second round trip rather than a guessed
    # job name folded into the first.
    if ($resolvedJobs.Count -eq 0) {
        Add-Check -Step '6b.15' -Name 'Scrape jobs have series (6b.6)' -Status 'Fail' -Detail 'no job was resolved in the Checks stage above - nothing to query'
    }
    else {
        $seriesScript = New-Object Collections.Generic.List[string]
        for ($i = 0; $i -lt $resolvedJobs.Count; $i++) {
            $seriesScript.Add("printf '\n--- job$i\n'")
            $rawPath = ConvertTo-PromQlRawPath -Query "count({job=`"$($resolvedJobs[$i].Job)`"})"
            $seriesScript.Add("sudo k3s kubectl get --raw '$rawPath' 2>/dev/null || echo {}")
        }
        $seriesScript.Add("printf '\n--- end\n'")

        $seriesProbe = Invoke-NodeSsh @ssh -Command ($seriesScript -join '; ') -ConnectTimeoutSec 30
        if ($seriesProbe.ExitCode -ne 0 -or $seriesProbe.StdOut -notmatch '--- end') {
            foreach ($entry in $resolvedJobs) {
                Add-Check -Step '6b.15' -Name "Scrape job has series: $($entry.Label) (6b.6)" -Status 'Fail' -Detail 'the Series round trip failed as a whole - nothing below was queried'
            }
        }
        else {
            for ($i = 0; $i -lt $resolvedJobs.Count; $i++) {
                $entry = $resolvedJobs[$i]
                $result = ConvertFrom-ProbeJson -Output $seriesProbe.StdOut -Name "job$i"
                # @(...) is not decoration: `count({job="..."})` returns
                # exactly one sample, and Get-Field returns its value, so
                # PowerShell unrolls the one-element array into a bare
                # PSCustomObject on the way out. Windows PowerShell 5.1 does
                # not synthesize .Count on one of those, so reading it under
                # Set-StrictMode ended the whole run here - before the HTTPS
                # stage below had reported anything at all.
                $resultValue = @(Get-Path $result 'data.result' | Where-Object { $_ })
                $count = 0
                if ($resultValue.Count -gt 0) {
                    $valuePair = @(Get-Field $resultValue[0] 'value')
                    if ($valuePair.Count -ge 2) { [void][int]::TryParse([string]$valuePair[1], [ref]$count) }
                }
                if ((Get-Field $result 'status') -ne 'success') {
                    Add-Check -Step '6b.15' -Name "Scrape job has series: $($entry.Label) (6b.6)" -Status 'Fail' -Detail "query against job=$($entry.Job) did not succeed"
                }
                elseif ($count -gt 0) {
                    Add-Check -Step '6b.15' -Name "Scrape job has series: $($entry.Label) (6b.6)" -Status 'Pass' -Detail "job=$($entry.Job): $count series"
                }
                else {
                    Add-Check -Step '6b.15' -Name "Scrape job has series: $($entry.Label) (6b.6)" -Status 'Fail' -Detail "job=$($entry.Job): 0 series"
                }
            }
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'HTTPS'
    # ---------------------------------------------------------------- #

    # Three direct connections to the VIP, exactly what a browser on the LAN
    # sees - not routed through the SSH probe above. Every hostname is only
    # reachable by address (5a.7): DNS is Phase 7's, so -ServerName is sent
    # as SNI/Host while the socket dials $ingressVip.
    if (-not $domain -or -not $ingressVip) {
        Add-Check -Step '6b.15' -Name 'HTTPS checks' -Status 'Fail' -Detail "DOMAIN='$domain', INGRESS_VIP='$ingressVip' - both must be set in aerie-cluster-config to run any of the checks below"
    }
    else {
        $hostnames = @("status.$domain", "logs.$domain", "metrics.$domain")
        foreach ($hostname in $hostnames) {
            $response = Invoke-HttpsGet -IPAddress $ingressVip -ServerName $hostname
            if (-not $response.Connected) {
                Add-Check -Step '6b.15' -Name "$hostname serves a production certificate (6b.9, 6b.12, 6b.5)" -Status 'Fail' -Detail "$($response.Error)"
                continue
            }
            $certReason = $null
            $certOk = Test-ProductionCertificate -Certificate $response.Certificate -Domain $domain -HostName $hostname -Reason ([ref]$certReason)
            if ($certOk) {
                Add-Check -Step '6b.15' -Name "$hostname serves a production certificate (6b.9, 6b.12, 6b.5)" -Status 'Pass' -Detail "expires $($response.Certificate.NotAfter.ToString('yyyy-MM-dd')), status=$($response.StatusCode)"
            }
            else {
                Add-Check -Step '6b.15' -Name "$hostname serves a production certificate (6b.9, 6b.12, 6b.5)" -Status 'Fail' -Detail $certReason
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
        "## Phase 6 observability gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
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
        '_The cluster plan, Phase 6b.15. Read-only: `kubectl get` and `kubectl get --raw` GETs proxied to in-cluster services, plus direct TLS handshakes against the VIP._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 6 observability gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names its check; docs/plans/swarm/phase-6-observability.md 6b.15 has the reasoning for each.'
    exit 1
}

Write-Host "Phase 6 observability gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'Re-run this after any change under deploy/cluster/observability/ -'
Write-Host 'it is read-only and safe at any time.'
exit 0
