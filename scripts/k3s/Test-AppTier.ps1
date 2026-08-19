<#
.SYNOPSIS
    Asserts 5b.14's checklist in one run, the same shape
    scripts/k3s/Test-ClusterPlatform.ps1 (3b.13) and scripts/k3s/Test-DataTier.ps1
    (4b.11) established. "Phase 5 is done" as a command rather than as a memory.

.DESCRIPTION
    Steps 5b.1-5b.13 each did their own work; this is 5b.14 itself - the gate,
    not a re-check of everything those steps already proved. It reads the
    cluster once over SSH (kubectl, and one dependent `exec ls` inside an api
    pod) and separately opens direct HTTPS connections to the four hostnames
    over the VIP, which is why this needs a runner with a LAN route to it -
    the same reason verify-cluster-platform.yml and verify-data-tier.yml pin
    `self-hosted`.

    Same three properties every phase gate here states and relies on:

    **It does not stop at the first failure.** Nothing here writes anything,
    so continuing past a surprise costs nothing and a table of two dozen
    findings is worth more than the first one.

    **A check it cannot evaluate is a failure, not a skip.** An absent object,
    a connection that never completes, or an unparseable response is "not
    proven", and a gate that reports that as anything but failure can be
    satisfied by a cluster that is off.

    **Expectations come from the cluster and the repository, not from
    parameters.** DOMAIN, the image registry and the share host come from the
    live aerie-cluster-config ConfigMap; the expected replica counts come from
    charts/aerie/values.yaml; the "no installation value leaked" and "no
    write-back tag leaked" greps run against the committed tree. Nothing here
    is a number to remember to pass, so a run months from now checks the same
    things this one does even after those files change.

    Stages:
      1. Preflight - the SSH key resolves, the client is present, the node
                      answers 22, and the chart's values.yaml parses.
      2. Probe      - one SSH round trip collects every cluster object the
                      checks below reason about, including one dependent
                      `kubectl exec ls` inside an api pod for the share mount.
      3. Checks     - 5b.14's checklist, evaluated against that snapshot.
      4. HTTPS      - four direct TLS connections to the VIP, one per
                      hostname, run from this machine rather than over SSH -
                      what a browser on the LAN actually sees.
      5. Portability- charts/ and deploy/cluster/apps/ grepped for the values
                      that must only ever appear as `${...}` substitutions,
                      plus the one grep this phase makes newly necessary: a
                      14-digit timestamped tag anywhere under deploy/ or
                      charts/ means the site repo is being bypassed.
      6. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here over SSH is
    cluster state, the same as Test-DataTier.ps1.

.EXAMPLE
    .\Test-AppTier.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$DeployPath = (Join-Path $PSScriptRoot '..\..\deploy'),
    [string]$ChartsPath = (Join-Path $PSScriptRoot '..\..\charts'),
    [string]$ValuesPath = (Join-Path $PSScriptRoot '..\..\charts\aerie\values.yaml'),

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# The one shape a resolved ImagePolicy tag can have (5b.12's filterTags):
# <UTC timestamp>-<short sha>. Named once so the resolved-tag check and the
# write-back-leak grep in the Portability stage can't drift apart.
$TimestampedTagPattern = '^\d{14}-[0-9a-f]+$'

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
        link is missing. Never used against a label name that itself
        contains a dot (e.g. cnpg.io/...) - every object below is reached by
        a server-side `-l`/name selector instead, for exactly that reason.
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

function Get-ChartReplicaCounts {
    <#
    .SYNOPSIS
        api's and files' replicaCount, read out of charts/aerie/values.yaml
        rather than hardcoded - so a values.yaml change (5b.11's own
        instruction to retune resources per hardware applies just as much to
        replica counts) doesn't leave this gate checking a stale number.

    .DESCRIPTION
        Line-matched, not YAML-parsed, for the same reason
        Test-DataTier.ps1's Get-YamlScalar is: this repository's own
        committed file, covered by ci.yml's `helm lint`, with exactly two
        keys of interest and a shape (a top-level scalar, and one more of the
        same name nested one level under `files:`) simple enough that a real
        parser would buy nothing a line match doesn't already have.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $result = [pscustomobject]@{ Api = $null; Files = $null }
    if (-not (Test-Path $Path -PathType Leaf)) { return $result }
    $inFiles = $false
    foreach ($line in (Get-Content -Path $Path)) {
        if ($line -match '^replicaCount:\s*(\d+)\s*$') { $result.Api = [int]$Matches[1]; continue }
        if ($line -match '^files:\s*$') { $inFiles = $true; continue }
        if ($inFiles) {
            if ($line -match '^\s+replicaCount:\s*(\d+)\s*$') { $result.Files = [int]$Matches[1]; $inFiles = $false; continue }
            if ($line -notmatch '^\s') { $inFiles = $false }
        }
    }
    return $result
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
        the status code, headers, a bounded body and the certificate the
        server presented.

    .DESCRIPTION
        Test-ClusterPlatform.ps1's Invoke-TlsProbe, extended to read a full
        response instead of one status line - 5b.14 needs the response
        *body* (the kiosk rewrite and the dufs auth challenge are both proven
        by what came back, not by the status line alone) as well as the
        code, and needs it for four different hostnames rather than one.

        -ServerName is sent as SNI and as the Host header while the TCP
        connection is made to an address, the same thing curl's --resolve and
        openssl's -servername do and for the same reason Invoke-TlsProbe's
        own docstring gives: no DNS anywhere in this phase points at the VIP
        yet (Phase 7), so resolving the name would test the wrong thing.

        Certificate validation is accepted unconditionally - the certificate
        is what several checks below inspect, not a precondition of the
        request completing.

        The body is read as ASCII up to -MaxBodyBytes and never decoded for
        Transfer-Encoding: chunked. Every body this script actually inspects
        text from (the dashboard's index.html, files' version.json) is served
        by UseStaticFiles/nginx, which always knows its Content-Length
        upfront and so is never chunked - a response that arrived chunked
        would fail its check by not containing the expected text, which is
        the correct outcome for a shape this function was never asked to
        parse.
    #>
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$ServerName,
        [string]$Path = '/',
        [int]$Port = 443,
        [int]$TimeoutMs = 15000,
        [int]$MaxBodyBytes = 262144
    )

    $result = [pscustomobject]@{
        Connected   = $false
        StatusCode  = $null
        Headers     = @{}
        Body        = ''
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
        $writer.WriteLine('User-Agent: aerie-test-app-tier')
        $writer.WriteLine('Connection: close')
        $writer.WriteLine()

        $reader = New-Object IO.StreamReader($ssl, [Text.Encoding]::ASCII)
        $statusLine = $reader.ReadLine()
        $result.Connected = $true
        if ($statusLine -match '^HTTP/\d\.\d\s+(\d{3})') { $result.StatusCode = [int]$Matches[1] }

        $line = $reader.ReadLine()
        while ($null -ne $line -and $line -ne '') {
            $separator = $line.IndexOf(':')
            if ($separator -gt 0) { $result.Headers[$line.Substring(0, $separator).Trim()] = $line.Substring($separator + 1).Trim() }
            $line = $reader.ReadLine()
        }

        $bodyBuilder = New-Object Text.StringBuilder
        $buffer = New-Object char[] 4096
        $read = 0
        while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0 -and $bodyBuilder.Length -lt $MaxBodyBytes) {
            [void]$bodyBuilder.Append($buffer, 0, $read)
        }
        $result.Body = $bodyBuilder.ToString()
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
        # Not -Host: that shadows the automatic $Host variable (the engine's
        # own host object) inside this function's scope, which costs nothing
        # here but is worth not doing anyway.
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

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    foreach ($path in @($DeployPath, $ChartsPath)) {
        if (-not (Test-Path $path -PathType Container)) { $failures.Add("Not found: '$path'. Run this from a checkout of the repository the cluster reconciles from.") }
    }
    if (-not (Test-Path $ValuesPath -PathType Leaf)) {
        $failures.Add("Not found: '$ValuesPath'. The expected replica-count checks read their expectation from this committed file rather than hardcoding it.")
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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-app-tier-gate'
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

    $chartReplicas = Get-ChartReplicaCounts -Path $ValuesPath

    Write-Host "Cluster:  $IPAddress"
    Write-Host "Repo:     $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host 'Preflight OK. Nothing below writes to the cluster - the exec in the probe is a read-only `ls`.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-app-tier-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip, the discipline every gate here documents at length:
    # every command falls back to `echo {}` (or `|| true` for a jsonpath
    # scalar) on an unregistered type or missing object, single quotes only -
    # Invoke-NodeSsh refuses a command containing a double quote, because
    # Windows PowerShell 5.1 lets ssh.exe strip it and run a subtly different
    # script on the node.
    #
    # Two dependent reads happen inside the remote shell itself, the same
    # pattern Test-DataTier.ps1 uses for its primary-pod psql exec: APIPOD and
    # SUBPATH are captured as shell variables and used unquoted, with no
    # `[ -n ... ]` guard - an unquoted empty-string test is a classic footgun
    # ([ -z ] alone, with nothing to test, is true), and quoting either
    # variable would need the double quote this command is forbidden from
    # carrying. An empty APIPOD just makes the exec's resource name empty,
    # which kubectl reports as an error the shareexec section captures like
    # any other output, rather than the probe as a whole.
    $probeScript = @(
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- nodes\n'''
        'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
        'printf ''\n--- helmrelease\n'''
        'sudo k3s kubectl -n flux-system get helmreleases.helm.toolkit.fluxcd.io aerie -o json 2>/dev/null || echo {}'
        'printf ''\n--- migratejob\n'''
        'sudo k3s kubectl -n aerie get job api-migrate -o json 2>/dev/null || echo {}'
        'printf ''\n--- deployments\n'''
        'sudo k3s kubectl -n aerie get deployments -o json 2>/dev/null || echo {}'
        'printf ''\n--- pods\n'''
        'sudo k3s kubectl -n aerie get pods -o json 2>/dev/null || echo {}'
        'printf ''\n--- pdbs\n'''
        'sudo k3s kubectl -n aerie get poddisruptionbudgets -o json 2>/dev/null || echo {}'
        'printf ''\n--- ingress\n'''
        'sudo k3s kubectl -n aerie get ingress -o json 2>/dev/null || echo {}'
        # Type only, never -o json over a Secret - the same rule every other
        # gate here follows: nothing that reaches a run log may carry
        # credential bytes.
        'printf ''\n--- ghcrpullaerietype\n'''
        'sudo k3s kubectl -n aerie get secret ghcr-pull -o jsonpath={.type} 2>/dev/null || true'
        'printf ''\n--- ghcrpullfluxtype\n'''
        'sudo k3s kubectl -n flux-system get secret ghcr-pull -o jsonpath={.type} 2>/dev/null || true'
        'printf ''\n--- pvcs\n'''
        'sudo k3s kubectl -n aerie get pvc aerie-share-rw aerie-share-ro -o json 2>/dev/null || echo {}'
        'printf ''\n--- sitegitrepository\n'''
        'sudo k3s kubectl -n flux-system get gitrepositories.source.toolkit.fluxcd.io aerie-site -o json 2>/dev/null || echo {}'
        'printf ''\n--- imagetags\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-image-tags -o json 2>/dev/null || echo {}'
        'printf ''\n--- imagepolicies\n'''
        'sudo k3s kubectl -n flux-system get imagepolicies.image.toolkit.fluxcd.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- imageupdateautomation\n'''
        'sudo k3s kubectl -n flux-system get imageupdateautomations.image.toolkit.fluxcd.io aerie -o json 2>/dev/null || echo {}'
        'APIPOD=$(sudo k3s kubectl -n aerie get pods -l app.kubernetes.io/component=api --field-selector=status.phase=Running -o jsonpath={.items[0].metadata.name} 2>/dev/null || true)'
        'SUBPATH=$(sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o jsonpath={.data.MEDIA_LIBRARY_SUBPATH} 2>/dev/null || true)'
        'printf ''\n--- shareexec\n'''
        'sudo k3s kubectl -n aerie exec $APIPOD -c api -- ls -A /share-ro/$SUBPATH 2>&1 || true'
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
    $helmRelease = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'helmrelease'
    $migrateJob = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'migratejob'
    $deployments = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'deployments'))
    $pods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pods'))
    $pdbs = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pdbs'))
    $ingresses = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'ingress'))
    $ghcrPullAerieType = (Get-ProbeSection -Output $probe.StdOut -Name 'ghcrpullaerietype').Trim()
    $ghcrPullFluxType = (Get-ProbeSection -Output $probe.StdOut -Name 'ghcrpullfluxtype').Trim()
    $pvcs = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pvcs'))
    $siteGitRepository = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'sitegitrepository'
    $imageTags = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'imagetags'
    $imagePolicies = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'imagepolicies'))
    $imageUpdateAutomation = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'imageupdateautomation'
    $shareExecOutput = (Get-ProbeSection -Output $probe.StdOut -Name 'shareexec').Trim()

    $configData = Get-Field $clusterConfig 'data'
    $configValues = @{}
    if ($configData) { foreach ($property in $configData.PSObject.Properties) { $configValues[$property.Name] = [string]$property.Value } }
    $domain = if ($configValues.ContainsKey('DOMAIN')) { $configValues['DOMAIN'] } else { $null }
    $ingressVip = if ($configValues.ContainsKey('INGRESS_VIP')) { $configValues['INGRESS_VIP'] } else { $null }
    $imageRegistry = if ($configValues.ContainsKey('IMAGE_REGISTRY')) { $configValues['IMAGE_REGISTRY'] } else { $null }
    $shareHost = if ($configValues.ContainsKey('SHARE_HOST')) { $configValues['SHARE_HOST'] } else { $null }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- HelmRelease Ready, and the migrate hook's last run Complete -----
    [void](Add-ObjectReadyCheck -Step '5b.14' -Name 'HelmRelease aerie' -Object $helmRelease)

    # Jobs carry a status.succeeded count, not a Ready condition - see
    # Test-DataTier.ps1's own ddlJob normalisation for why the condition
    # object below is built as a [pscustomobject] and not a bare hashtable:
    # Get-Field reads via .PSObject.Properties, which a [hashtable]'s own
    # dictionary keys never populate.
    $migrateJobNormalized = $null
    if ($migrateJob -and [int](Get-Path $migrateJob 'status.succeeded') -ge 1) {
        $migrateJobNormalized = [pscustomobject]@{ status = [pscustomobject]@{ conditions = @([pscustomobject]@{ type = 'Ready'; status = 'True'; reason = 'Complete' }) } }
    }
    $migrateJobMissingDetail = if ($migrateJob) { "not complete - succeeded=$(Get-Path $migrateJob 'status.succeeded'), failed=$(Get-Path $migrateJob 'status.failed')" } else { 'not found - the migrate hook only exists between the start of one release and the next (before-hook-creation)' }
    [void](Add-ObjectReadyCheck -Step '5b.14' -Name 'Job api-migrate' -Object $migrateJobNormalized -MissingDetail $migrateJobMissingDetail)

    # --- api 3/3, files 2/2, spread across distinct nodes, no restarts ---
    $totalNodes = $nodes.Count
    foreach ($component in @('api', 'files')) {
        $expected = if ($component -eq 'api') { $chartReplicas.Api } else { $chartReplicas.Files }
        $deployment = $deployments | Where-Object { (Get-Path $_ 'metadata.name') -eq $component } | Select-Object -First 1
        # Get-Field, not Get-Path, for the label lookup: the label's own name
        # contains dots ('app.kubernetes.io/component'), and Get-Path splits
        # on every '.' with no escaping - see its own docstring. Get-Field
        # takes the name as one literal string instead.
        $componentPods = @($pods | Where-Object { (Get-Field (Get-Path $_ 'metadata.labels') 'app.kubernetes.io/component') -eq $component })

        if (-not $expected) {
            Add-Check -Step '5b.14' -Name "Deployment $component replicas" -Status 'Fail' -Detail "couldn't read replicaCount for '$component' out of $ValuesPath"
        }
        elseif ($null -eq $deployment) {
            Add-Check -Step '5b.14' -Name "Deployment $component replicas" -Status 'Fail' -Detail 'not found in namespace aerie'
        }
        else {
            $ready = [int](Get-Path $deployment 'status.readyReplicas')
            $specReplicas = [int](Get-Path $deployment 'spec.replicas')
            if ($specReplicas -eq $expected -and $ready -eq $expected) {
                Add-Check -Step '5b.14' -Name "Deployment $component replicas" -Status 'Pass' -Detail "$ready/$expected ready"
            }
            else {
                Add-Check -Step '5b.14' -Name "Deployment $component replicas" -Status 'Fail' -Detail "spec.replicas=$specReplicas, readyReplicas=$ready, want $expected (charts/aerie/values.yaml)"
            }
        }

        if ($componentPods.Count -eq 0) {
            Add-Check -Step '5b.14' -Name "$component pods spread across nodes" -Status 'Fail' -Detail 'no pods found'
        }
        else {
            $nodeNames = @($componentPods | ForEach-Object { [string](Get-Path $_ 'spec.nodeName') } | Where-Object { $_ })
            $distinct = @($nodeNames | Select-Object -Unique)
            if ($totalNodes -le 1) {
                Add-Check -Step '5b.14' -Name "$component pods spread across nodes" -Status 'Warn' -Detail "cluster reports only $totalNodes node(s) - spread can't be evaluated until a second exists"
            }
            elseif ($distinct.Count -le 1) {
                Add-Check -Step '5b.14' -Name "$component pods spread across nodes" -Status 'Fail' -Detail "$($componentPods.Count) pod(s) all on $(if ($distinct.Count -eq 1) { $distinct[0] } else { 'no scheduled node' }), out of $totalNodes node(s) in the cluster - topologySpreadConstraints (5b.11) did not apply"
            }
            else {
                Add-Check -Step '5b.14' -Name "$component pods spread across nodes" -Status 'Pass' -Detail "$($componentPods.Count) pod(s) across $($distinct.Count) node(s): $($nodeNames -join ', ')"
            }
        }

        $restarted = @($componentPods | ForEach-Object {
                $podName = Get-Path $_ 'metadata.name'
                $statuses = @(Get-Path $_ 'status.containerStatuses' | Where-Object { $_ })
                foreach ($status in $statuses) {
                    if ([int](Get-Field $status 'restartCount') -gt 0) { "$podName/$(Get-Field $status 'name') (x$(Get-Field $status 'restartCount'))" }
                }
            } | Where-Object { $_ })
        if ($componentPods.Count -eq 0) {
            Add-Check -Step '5b.14' -Name "$component containers never restarted" -Status 'Fail' -Detail 'no pods found'
        }
        elseif ($restarted.Count -gt 0) {
            Add-Check -Step '5b.14' -Name "$component containers never restarted" -Status 'Fail' -Detail ($restarted -join ', ')
        }
        else {
            Add-Check -Step '5b.14' -Name "$component containers never restarted" -Status 'Pass' -Detail "$($componentPods.Count) pod(s), 0 restarts"
        }
    }

    # --- ghcr-pull in both namespaces, dockerconfigjson; no ImagePullBackOff
    if ($ghcrPullAerieType -eq 'kubernetes.io/dockerconfigjson') {
        Add-Check -Step '5b.14' -Name 'Secret ghcr-pull (aerie)' -Status 'Pass' -Detail $ghcrPullAerieType
    }
    else {
        Add-Check -Step '5b.14' -Name 'Secret ghcr-pull (aerie)' -Status 'Fail' -Detail "type is '$ghcrPullAerieType' - expected kubernetes.io/dockerconfigjson (not found reads as an empty type here)"
    }
    if ($ghcrPullFluxType -eq 'kubernetes.io/dockerconfigjson') {
        Add-Check -Step '5b.14' -Name 'Secret ghcr-pull (flux-system)' -Status 'Pass' -Detail $ghcrPullFluxType
    }
    else {
        Add-Check -Step '5b.14' -Name 'Secret ghcr-pull (flux-system)' -Status 'Fail' -Detail "type is '$ghcrPullFluxType' - expected kubernetes.io/dockerconfigjson"
    }

    $backOff = @($pods | ForEach-Object {
            $podName = Get-Path $_ 'metadata.name'
            $statuses = @(Get-Path $_ 'status.containerStatuses' | Where-Object { $_ })
            foreach ($status in $statuses) {
                $reason = Get-Path $status 'state.waiting.reason'
                if ($reason -in @('ImagePullBackOff', 'ErrImagePull')) { "$podName/$(Get-Field $status 'name'): $reason" }
            }
        } | Where-Object { $_ })
    if ($backOff.Count -gt 0) {
        Add-Check -Step '5b.14' -Name 'No ImagePullBackOff in aerie' -Status 'Fail' -Detail ($backOff -join ', ')
    }
    else {
        Add-Check -Step '5b.14' -Name 'No ImagePullBackOff in aerie' -Status 'Pass' -Detail "$($pods.Count) pod(s) checked"
    }

    # --- every container in aerie declares resources.requests -----------
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
    if ($pods.Count -eq 0) {
        Add-Check -Step '5b.14' -Name 'Every container declares resources.requests' -Status 'Fail' -Detail 'no pods found in namespace aerie'
    }
    elseif ($undeclared.Count -gt 0) {
        Add-Check -Step '5b.14' -Name 'Every container declares resources.requests' -Status 'Fail' -Detail "missing cpu and/or memory requests: $($undeclared -join ', ')"
    }
    else {
        $containerCount = ($pods | ForEach-Object { @(Get-Path $_ 'spec.containers' | Where-Object { $_ }).Count } | Measure-Object -Sum).Sum
        Add-Check -Step '5b.14' -Name 'Every container declares resources.requests' -Status 'Pass' -Detail "$containerCount container(s) across $($pods.Count) pod(s)"
    }

    # --- both PDBs exist, disruptionsAllowed > 0 -------------------------
    foreach ($name in @('api', 'files')) {
        $pdb = $pdbs | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        if ($null -eq $pdb) {
            Add-Check -Step '5b.14' -Name "PodDisruptionBudget $name" -Status 'Fail' -Detail 'not found'
        }
        else {
            $allowed = [int](Get-Path $pdb 'status.disruptionsAllowed')
            if ($allowed -gt 0) {
                Add-Check -Step '5b.14' -Name "PodDisruptionBudget $name" -Status 'Pass' -Detail "disruptionsAllowed=$allowed"
            }
            else {
                Add-Check -Step '5b.14' -Name "PodDisruptionBudget $name" -Status 'Fail' -Detail "disruptionsAllowed=$allowed - a drain would block here"
            }
        }
    }

    # --- four Ingress objects admitted by Traefik -------------------------
    # Traefik publishes the LoadBalancer Service's address onto every
    # Ingress's own status - k3s's bundled chart sets
    # providers.kubernetesIngress.publishedService.enabled: true
    # (traefik-helmchartconfig.yaml's own header comment confirms this
    # against the pinned chart version) - so "admitted" is checked against
    # that address rather than against a status shape Traefik doesn't
    # otherwise populate.
    if (-not $ingressVip) {
        Add-Check -Step '5b.14' -Name 'Ingress objects admitted' -Status 'Fail' -Detail 'INGRESS_VIP is not in aerie-cluster-config'
    }
    else {
        foreach ($name in @('home', 'kiosk', 'files', 'share')) {
            $ingress = $ingresses | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
            if ($null -eq $ingress) {
                Add-Check -Step '5b.14' -Name "Ingress $name admitted" -Status 'Fail' -Detail 'not found'
                continue
            }
            $lbIngress = @(Get-Path $ingress 'status.loadBalancer.ingress' | Where-Object { $_ })
            $lbIps = @($lbIngress | ForEach-Object { [string](Get-Field $_ 'ip') } | Where-Object { $_ })
            if ($lbIps -contains $ingressVip) {
                Add-Check -Step '5b.14' -Name "Ingress $name admitted" -Status 'Pass' -Detail "status.loadBalancer.ingress: $($lbIps -join ', ')"
            }
            else {
                Add-Check -Step '5b.14' -Name "Ingress $name admitted" -Status 'Fail' -Detail "status.loadBalancer.ingress: $(if ($lbIps.Count -gt 0) { $lbIps -join ', ' } else { '(none)' }) - want $ingressVip"
            }
        }
    }

    # --- both SMB PVCs Bound, and the share mount actually lists something
    foreach ($name in @('aerie-share-rw', 'aerie-share-ro')) {
        $pvc = $pvcs | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        $phase = [string](Get-Path $pvc 'status.phase')
        if ($phase -eq 'Bound') {
            Add-Check -Step '5b.14' -Name "PVC $name" -Status 'Pass' -Detail 'Bound'
        }
        else {
            Add-Check -Step '5b.14' -Name "PVC $name" -Status 'Fail' -Detail "phase is '$phase', not Bound"
        }
    }
    $mediaSubpath = if ($configValues.ContainsKey('MEDIA_LIBRARY_SUBPATH')) { $configValues['MEDIA_LIBRARY_SUBPATH'] } else { '' }
    if ([string]::IsNullOrWhiteSpace($shareExecOutput)) {
        Add-Check -Step '5b.14' -Name 'Share mount lists something' -Status 'Fail' -Detail 'empty result from `ls -A /share-ro/...` inside an api pod - no Running api pod, or the mount is empty'
    }
    elseif ($shareExecOutput -match '^(error|command terminated)') {
        Add-Check -Step '5b.14' -Name 'Share mount lists something' -Status 'Fail' -Detail $shareExecOutput
    }
    else {
        $where = if ($mediaSubpath) { "/share-ro/$mediaSubpath" } else { '/share-ro (MEDIA_LIBRARY_SUBPATH unset, checked the mount root)' }
        Add-Check -Step '5b.14' -Name 'Share mount lists something' -Status 'Pass' -Detail "$where`: $($shareExecOutput -replace '\s+', ' ')"
    }

    # --- aerie-site GitRepository Ready; aerie-image-tags carries both keys
    [void](Add-ObjectReadyCheck -Step '5b.14' -Name 'GitRepository aerie-site' -Object $siteGitRepository)
    $tagData = Get-Field $imageTags 'data'
    $apiTag = [string](Get-Field $tagData 'AERIE_API_IMAGE_TAG')
    $filesTag = [string](Get-Field $tagData 'KIOSK_FILES_IMAGE_TAG')
    if ($tagData -and $apiTag -and $filesTag) {
        Add-Check -Step '5b.14' -Name 'ConfigMap aerie-image-tags' -Status 'Pass' -Detail "AERIE_API_IMAGE_TAG=$apiTag, KIOSK_FILES_IMAGE_TAG=$filesTag"
    }
    else {
        Add-Check -Step '5b.14' -Name 'ConfigMap aerie-image-tags' -Status 'Fail' -Detail "not found in flux-system, or a key is empty (AERIE_API_IMAGE_TAG='$apiTag', KIOSK_FILES_IMAGE_TAG='$filesTag') - the apps layer substitutes from this, so a missing/stale one is an empty tag rather than an error"
    }

    # --- both ImagePolicy objects resolved; last automation run succeeded
    foreach ($name in @('aerie-api', 'kiosk-files')) {
        $policy = $imagePolicies | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        if ($null -eq $policy) {
            Add-Check -Step '5b.14' -Name "ImagePolicy $name" -Status 'Fail' -Detail 'not found'
            continue
        }
        $resolvedTag = [string](Get-Path $policy 'status.latestRef.tag')
        if ($resolvedTag -match $TimestampedTagPattern) {
            Add-Check -Step '5b.14' -Name "ImagePolicy $name" -Status 'Pass' -Detail "resolved to $resolvedTag"
        }
        else {
            Add-Check -Step '5b.14' -Name "ImagePolicy $name" -Status 'Fail' -Detail "status.latestRef.tag='$resolvedTag' - no tag resolved yet. Push at least one build (publish.yml) before this can pass"
        }
    }
    [void](Add-ObjectReadyCheck -Step '5b.14' -Name 'ImageUpdateAutomation aerie' -Object $imageUpdateAutomation)

    # ---------------------------------------------------------------- #
    Write-Stage 'HTTPS'
    # ---------------------------------------------------------------- #

    # Four direct connections to the VIP, exactly what a browser on the LAN
    # sees - not routed through the SSH probe above, which only reasons
    # about cluster objects. Every hostname is only reachable by address
    # (5a.7): DNS is Phase 7's, so -ServerName is sent as SNI/Host while the
    # socket dials $ingressVip.
    if (-not $domain -or -not $ingressVip) {
        Add-Check -Step '5b.14' -Name 'HTTPS checks' -Status 'Fail' -Detail "DOMAIN='$domain', INGRESS_VIP='$ingressVip' - both must be set in aerie-cluster-config to run any of the checks below"
    }
    else {
        # One GET per hostname - home's on /health/ready (Program.cs's 5b.2
        # split retired the bare /health 5a.7's own example predates), the
        # other three on / - and two checks from each response: "does this
        # hostname answer with the production wildcard" (every host, the
        # same certificate) and "does it answer with what *this* host is
        # supposed to serve" (different per host, and not always 200 - dufs
        # challenging is the pass for share, not the failure).
        $hosts = @(
            [pscustomobject]@{ Name = "home.$domain"; Path = '/health/ready' }
            [pscustomobject]@{ Name = "kiosk.$domain"; Path = '/' }
            [pscustomobject]@{ Name = "files.$domain"; Path = '/version.json' }
            [pscustomobject]@{ Name = "share.$domain"; Path = '/' }
        )
        $responses = @{}
        foreach ($entry in $hosts) {
            $response = Invoke-HttpsGet -IPAddress $ingressVip -ServerName $entry.Name -Path $entry.Path
            $responses[$entry.Name] = $response
            if (-not $response.Connected) {
                Add-Check -Step '5b.14' -Name "$($entry.Name) serves a production certificate" -Status 'Fail' -Detail "$($response.Error)"
            }
            else {
                $certReason = $null
                $certOk = Test-ProductionCertificate -Certificate $response.Certificate -Domain $domain -HostName $entry.Name -Reason ([ref]$certReason)
                if ($certOk) {
                    Add-Check -Step '5b.14' -Name "$($entry.Name) serves a production certificate" -Status 'Pass' -Detail "expires $($response.Certificate.NotAfter.ToString('yyyy-MM-dd'))"
                }
                else {
                    Add-Check -Step '5b.14' -Name "$($entry.Name) serves a production certificate" -Status 'Fail' -Detail $certReason
                }
            }
        }

        # home: connected and a 200 on /health/ready is the whole check - the
        # certificate above already covers TLS.
        $homeResponse = $responses["home.$domain"]
        if (-not $homeResponse.Connected) {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Fail' -Detail "$($homeResponse.Error)"
        }
        elseif ($homeResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Pass' -Detail '200 on /health/ready'
        }
        else {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Fail' -Detail "status=$($homeResponse.StatusCode)"
        }

        # kiosk: the root path, rewritten server-side by middleware-kiosk.yaml
        # (5b.9) before it reaches api - a 200 alone doesn't prove the
        # rewrite fired, since the unrewritten API landing page also answers
        # 200, so this checks the body for the dashboard SPA's own <title>
        # rather than the status code.
        $kioskResponse = $responses["kiosk.$domain"]
        if (-not $kioskResponse.Connected) {
            Add-Check -Step '5b.14' -Name "kiosk.$domain serves the dashboard" -Status 'Fail' -Detail "$($kioskResponse.Error)"
        }
        elseif ($kioskResponse.StatusCode -eq 200 -and $kioskResponse.Body -match '<title>\s*Aerie Dashboard') {
            Add-Check -Step '5b.14' -Name "kiosk.$domain serves the dashboard" -Status 'Pass' -Detail 'body carries the dashboard SPA title'
        }
        elseif ($kioskResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "kiosk.$domain serves the dashboard" -Status 'Fail' -Detail '200, but the body is not the dashboard - the kiosk-root-rewrite Middleware is not firing, so this is the unrewritten API landing page'
        }
        else {
            Add-Check -Step '5b.14' -Name "kiosk.$domain serves the dashboard" -Status 'Fail' -Detail "status=$($kioskResponse.StatusCode)"
        }

        # files: version.json above, plus the APK - the two paths every
        # installed tablet's UpdateManager and provisioning QR payload
        # hardcode (docs/kiosk-architecture.md).
        $versionResponse = $responses["files.$domain"]
        if (-not $versionResponse.Connected) {
            Add-Check -Step '5b.14' -Name "files.$domain/version.json serves" -Status 'Fail' -Detail "$($versionResponse.Error)"
        }
        elseif ($versionResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "files.$domain/version.json serves" -Status 'Pass' -Detail '200'
        }
        else {
            Add-Check -Step '5b.14' -Name "files.$domain/version.json serves" -Status 'Fail' -Detail "status=$($versionResponse.StatusCode)"
        }
        $apkResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "files.$domain" -Path '/app-release.apk'
        if (-not $apkResponse.Connected) {
            Add-Check -Step '5b.14' -Name "files.$domain/app-release.apk serves" -Status 'Fail' -Detail "$($apkResponse.Error)"
        }
        elseif ($apkResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "files.$domain/app-release.apk serves" -Status 'Pass' -Detail '200'
        }
        else {
            Add-Check -Step '5b.14' -Name "files.$domain/app-release.apk serves" -Status 'Fail' -Detail "status=$($apkResponse.StatusCode)"
        }

        # share: dufs's --auth rule (share-deployment.yaml) must challenge an
        # anonymous GET rather than list the share - 200 here is the failure
        # mode, not the pass.
        $shareResponse = $responses["share.$domain"]
        if (-not $shareResponse.Connected) {
            Add-Check -Step '5b.14' -Name "share.$domain challenges for authentication" -Status 'Fail' -Detail "$($shareResponse.Error)"
        }
        elseif ($shareResponse.StatusCode -eq 401) {
            Add-Check -Step '5b.14' -Name "share.$domain challenges for authentication" -Status 'Pass' -Detail "401$(if ($shareResponse.Headers['WWW-Authenticate']) { ", WWW-Authenticate: $($shareResponse.Headers['WWW-Authenticate'])" })"
        }
        elseif ($shareResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "share.$domain challenges for authentication" -Status 'Fail' -Detail '200 - the share is listing anonymously; --auth (share-deployment.yaml) is not being enforced'
        }
        else {
            Add-Check -Step '5b.14' -Name "share.$domain challenges for authentication" -Status 'Fail' -Detail "status=$($shareResponse.StatusCode), expected 401"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Portability'
    # ---------------------------------------------------------------- #

    # 5b.14's own instruction: run the portability check over charts/ and
    # deploy/cluster/apps/ specifically - the narrower scope, alongside
    # ci.yml's whole-deploy/ sweep and Test-ClusterPlatform.ps1's live-value
    # sweep, is because this phase is the first to put installation values
    # inside a Helm values: block as well as inside plain manifests, and
    # nothing upstream has ever grepped charts/ at all.
    $portabilityFiles = @(Get-ChildItem -Path $ChartsPath -Recurse -File) + @(Get-ChildItem -Path (Join-Path $DeployPath 'cluster\apps') -Recurse -File)
    $portabilityValues = @{ DOMAIN = $domain; IMAGE_REGISTRY = $imageRegistry; SHARE_HOST = $shareHost }
    $leaks = New-Object Collections.Generic.List[string]
    foreach ($key in $portabilityValues.Keys) {
        $value = $portabilityValues[$key]
        if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -lt 4) { continue }
        $hits = @($portabilityFiles | Select-String -SimpleMatch -Pattern $value -ErrorAction SilentlyContinue)
        foreach ($hit in $hits) {
            $leaks.Add("$key at $($hit.Path):$($hit.LineNumber)")
        }
    }
    if (-not $domain -or -not $imageRegistry -or -not $shareHost) {
        Add-Check -Step '5b.14' -Name 'No installation values in charts/ or deploy/cluster/apps/' -Status 'Fail' -Detail 'DOMAIN, IMAGE_REGISTRY or SHARE_HOST is missing from aerie-cluster-config, so this could not run with real values'
    }
    elseif ($leaks.Count -gt 0) {
        Add-Check -Step '5b.14' -Name 'No installation values in charts/ or deploy/cluster/apps/' -Status 'Fail' -Detail "$($leaks -join '; '). Belongs as a `${...} substitution or a Helm value - see docs/ethos.md"
    }
    else {
        Add-Check -Step '5b.14' -Name 'No installation values in charts/ or deploy/cluster/apps/' -Status 'Pass' -Detail "$($portabilityFiles.Count) file(s) checked against $($portabilityValues.Keys.Count) value(s)"
    }

    $addressHits = @($portabilityFiles | Select-String -Pattern '\b([0-9]{1,3}\.){3}[0-9]{1,3}\b' -ErrorAction SilentlyContinue)
    if ($addressHits.Count -gt 0) {
        $addressLeaks = @($addressHits | ForEach-Object { "$($_.Path):$($_.LineNumber)" })
        Add-Check -Step '5b.14' -Name 'No address literal in charts/ or deploy/cluster/apps/' -Status 'Fail' -Detail "$($addressLeaks -join '; ')"
    }
    else {
        Add-Check -Step '5b.14' -Name 'No address literal in charts/ or deploy/cluster/apps/' -Status 'Pass' -Detail "$($portabilityFiles.Count) file(s) checked"
    }

    # The one grep this phase makes newly necessary: a 14-digit timestamped
    # tag anywhere under deploy/ or charts/ (the wider tree - unlike the two
    # checks above, this one has nothing to do with per-installation values
    # and everything to do with 5a.3's own boundary) means image automation
    # wrote a concrete tag back into this repository instead of the site
    # repo, which is exactly the write-back the re-scope preamble rejected.
    $allTreeFiles = @(Get-ChildItem -Path $DeployPath -Recurse -File) + @(Get-ChildItem -Path $ChartsPath -Recurse -File)
    $tagHits = @($allTreeFiles | Select-String -Pattern $TimestampedTagPattern -ErrorAction SilentlyContinue)
    if ($tagHits.Count -gt 0) {
        $tagLeaks = @($tagHits | ForEach-Object { "$($_.Path):$($_.LineNumber)" })
        Add-Check -Step '5b.14' -Name 'No timestamped image tag under deploy/ or charts/' -Status 'Fail' -Detail "$($tagLeaks -join '; '). This value belongs in the site repo's image-tags.yaml (5a.3, 5b.12) and nowhere in this repository"
    }
    else {
        Add-Check -Step '5b.14' -Name 'No timestamped image tag under deploy/ or charts/' -Status 'Pass' -Detail "$($allTreeFiles.Count) file(s) checked"
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
        "## Phase 5 app tier gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
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
        '_The cluster plan, Phase 5b.14. Read-only except for one local `kubectl exec ... ls` inside an api pod._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 5 app tier gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names its check; docs/plans/swarm/phase-5-app-tier.md 5b.14 has the reasoning for each.'
    exit 1
}

Write-Host "Phase 5 app tier gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'Re-run this after any change under charts/, deploy/cluster/apps/ or deploy/cluster/site/ -'
Write-Host 'it is read-only (bar one local `ls`) and safe at any time.'
exit 0
