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

    **The wall is checked in both directions.** docs/auth-architecture.md's
    AUTH_MODE - read from the same live ConfigMap as everything else, absent
    meaning none - decides what the auth checks assert rather than whether
    they run. At none they assert the Middleware and the annotations are
    *absent*, because none is the documented rollback and a rollback that
    leaves half the wall standing is not one; at "canary" they assert the
    wall stands in front of apps/docs and nowhere else, which is the
    containment the phase exists to prove; at full they assert home and kiosk
    refuse an un-enrolled client while the allow-list - /health/ready, /media,
    and the files. and share. hosts - is still answered. Several of these
    cannot be answered by reading objects at all - a route whose middleware
    annotation does not resolve serves unauthenticated, and looks from every
    angle but an actual request like success - so they are HTTPS checks.

    One direction this cannot assert on its own: that an *enrolled* device is
    served. A grant is stored as a hash and this script writes nothing, so the
    credential has to be handed in - see -GrantToken, and the warning that
    stands in its place when it isn't.

    **Expectations come from the cluster and the repository, not from
    parameters.** -GrantToken is not an exception to this: it is a credential
    to present, not a value to compare against, and what the check does with
    it still comes from AUTH_MODE. DOMAIN, the image registry and the share
    host come from the
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
      4. HTTPS      - direct TLS connections to the VIP, run from this
                      machine rather than over SSH - what a browser on the
                      LAN actually sees. One per hostname, plus the four the
                      wall needs (below).
      5. Portability- charts/ and deploy/cluster/apps/ grepped for the values
                      that must only ever appear as `${...}` substitutions,
                      plus the one grep this phase makes newly necessary: a
                      14-digit timestamped tag anywhere under deploy/ or
                      charts/ means the site repo is being bypassed.
      6. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here over SSH is
    cluster state, the same as Test-DataTier.ps1.

.PARAMETER GrantToken
    A live grant token (docs/auth-architecture.md), which turns the one check here
    that needs a credential from a warning into a real assertion: at
    AUTH_MODE=full, an *enrolled* device is served rather than refused.

    It is a parameter, and optional, for a reason each half of which matters.
    A parameter, because this script writes nothing and a grant cannot be read
    back out of the database - it is stored only as a SHA-256, so the only way
    to hold a usable token is to have been issued one. Optional, because a
    gate that cannot run without a credential is a gate nobody runs; absent,
    the enrolled-device check is a Warn that names itself, and the refusal
    half - which needs no credential and is the half that can lock the house
    out - is asserted either way.

    Read it out of the __Secure-aerie_grant cookie of a browser that is
    already enrolled, or redeem an invite from the admin app's Sessions page
    for one issued to this check specifically and revoked afterwards.

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
    [string]$SshPrivateKey,

    [string]$GrantToken
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

        -Accept sets the Accept header, and leaving it unset is a deliberate
        case rather than a default: the wall answers a document request with a
        302 and everything else with a 401, so the two auth checks below reach
        both answers by setting it and not setting it.

        -Cookie sets a raw Cookie header, and exists for exactly one caller:
        the phase 6 check that an *enrolled* device is served rather than
        refused. Every other check here is the un-enrolled case, which is the
        default because it is the one a browser on the LAN presents by
        accident; proving the other direction needs a credential, and the
        credential is a parameter to this script rather than something it can
        mint, because minting one would be a write.

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
        [string]$Accept,
        [string]$Cookie,
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
        # Sent only when asked for, because its absence is itself a case the
        # auth checks below rely on: AuthChallenge content-negotiates the
        # refusal, so a caller that never says it can read text/html gets the
        # 401 a fetch can see rather than the 302 a browser follows. Passing
        # -Accept 'text/html' is how a check asks for the browser's answer.
        if ($Accept) { $writer.WriteLine("Accept: $Accept") }
        if ($Cookie) { $writer.WriteLine("Cookie: $Cookie") }
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
        'printf ''\n--- middlewares\n'''
        'sudo k3s kubectl -n aerie get middlewares.traefik.io -o json 2>/dev/null || echo {}'
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
    $middlewares = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'middlewares'))
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
    # docs/auth-architecture.md. Absent means 'none' - the same default the HelmRelease
    # substitutes and the chart carries, so this reads the cluster's actual
    # posture rather than a parameter someone remembered to pass. Every auth
    # check below is written against this value, so a run on an install with no
    # wall asserts that there is no wall, rather than skipping.
    $authMode = if ($configValues.ContainsKey('AUTH_MODE') -and $configValues['AUTH_MODE']) { $configValues['AUTH_MODE'].Trim().ToLowerInvariant() } else { 'none' }
    # The second axis: whether Person.IsAdmin decides anything. Read the same
    # way and defaulted the same way, so an install that never set it asserts
    # the pre-flag posture rather than skipping.
    $adminMode = if ($configValues.ContainsKey('ADMIN_MODE') -and $configValues['ADMIN_MODE']) { $configValues['ADMIN_MODE'].Trim().ToLowerInvariant() } else { 'none' }

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

        # Restarts, classified by what killed the container rather than
        # counted. A cumulative restartCount cannot stay at zero in this
        # cluster: Phase 1 staggers a weekly reboot across the Hyper-V hosts
        # (scripts/hyperv/Set-UpdateRebootSchedule.ps1), the guest goes down
        # with its host, and every container that was running there comes
        # back with its count incremented - the pod object itself survives a
        # reboot short enough to stay inside the node-monitor grace period,
        # so that count is history the workload had no say in. Asserting
        # `restartCount -eq 0` fails on any pod old enough to have seen one
        # reboot, which is a gate that goes red on a healthy cluster and
        # teaches everyone reading it to ignore the row.
        #
        # What 5b.14 means by this check is "nothing here is crashing", and
        # the last termination record is what distinguishes the two:
        #   - reason Unknown with exit code 255 - the containerd shim went
        #     away with the node. kubelet is reporting that it cannot account
        #     for the exit, not that the container failed; a process that
        #     exits on its own reports Error or Completed with its own code,
        #     never Unknown. This is the node-lifecycle signature, and it
        #     passes with the count and timestamp spelled out in the detail.
        #   - reason OOMKilled - 5b.11's memory sizing regressed. A failure
        #     whenever it happened.
        #   - anything else, a missing record, or a container sitting in
        #     CrashLoopBackOff right now - the workload failed. A failure.
        # Kubernetes keeps exactly one previous state per container, so a
        # restart older than the most recent one cannot be classified from
        # pod status at all; CrashLoopBackOff is what still catches the case
        # that matters now, and the detail below never claims more than the
        # one record it actually read.
        $restartFaults = [System.Collections.Generic.List[string]]::new()
        $restartReboots = [System.Collections.Generic.List[string]]::new()
        foreach ($pod in $componentPods) {
            $podName = Get-Path $pod 'metadata.name'
            $statuses = @(Get-Path $pod 'status.containerStatuses' | Where-Object { $_ })
            foreach ($status in $statuses) {
                $containerName = Get-Field $status 'name'
                $count = [int](Get-Field $status 'restartCount')
                $waitingReason = [string](Get-Path $status 'state.waiting.reason')
                if ($waitingReason -eq 'CrashLoopBackOff') {
                    $restartFaults.Add("$podName/$containerName in CrashLoopBackOff (x$count)")
                    continue
                }
                if ($count -le 0) { continue }

                $terminated = Get-Path $status 'lastState.terminated'
                $reason = [string](Get-Field $terminated 'reason')
                $exitCode = Get-Field $terminated 'exitCode'
                # ConvertFrom-Json turns an RFC 3339 timestamp into a
                # [datetime] in the runner's local zone, so printing it raw
                # would report this cluster's UTC-only clock in whichever
                # culture the runner happens to carry. Back to UTC, in the
                # one format, whether it arrived parsed or as a string.
                $finishedAt = Get-Field $terminated 'finishedAt'
                $finishedText = if ($finishedAt -is [datetime]) { $finishedAt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } elseif ($finishedAt) { [string]$finishedAt } else { '-' }
                $where = "$podName/$containerName (x$count, last: $(if ($reason) { $reason } else { 'no record' })/$(if ($null -ne $exitCode) { $exitCode } else { '-' }) at $finishedText)"

                if ($null -eq $terminated) {
                    $restartFaults.Add($where)
                }
                elseif ($reason -eq 'Unknown' -and [int]$exitCode -eq 255) {
                    $restartReboots.Add($where)
                }
                else {
                    $restartFaults.Add($where)
                }
            }
        }
        if ($componentPods.Count -eq 0) {
            Add-Check -Step '5b.14' -Name "$component containers not crash-restarting" -Status 'Fail' -Detail 'no pods found'
        }
        elseif ($restartFaults.Count -gt 0) {
            Add-Check -Step '5b.14' -Name "$component containers not crash-restarting" -Status 'Fail' -Detail ($restartFaults -join ', ')
        }
        elseif ($restartReboots.Count -gt 0) {
            Add-Check -Step '5b.14' -Name "$component containers not crash-restarting" -Status 'Pass' -Detail "$($componentPods.Count) pod(s), no crash restarts; $($restartReboots.Count) container(s) restarted with the node-lifecycle signature: $($restartReboots -join ', ')"
        }
        else {
            Add-Check -Step '5b.14' -Name "$component containers not crash-restarting" -Status 'Pass' -Detail "$($componentPods.Count) pod(s), 0 restarts"
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
        # docs-canary joins the list at auth.mode=canary and leaves again at
        # full, where the wall moves onto home and kiosk themselves.
        $expectedIngresses = @('home', 'kiosk', 'files', 'share')
        if ($authMode -eq 'canary') { $expectedIngresses += 'docs-canary' }
        foreach ($name in $expectedIngresses) {
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

    # --- the wall: one Middleware, and exactly the routes that carry it ---
    # docs/auth-architecture.md. Every assertion here is two-sided: at none
    # the objects must be *absent*, because none is the documented rollback
    # and a rollback that leaves half the wall standing is not one. An Ingress
    # annotated onto a Middleware that does not exist is a 500 on every request
    # through it, so the two guards have to agree, and this is where that is
    # checked rather than assumed.
    $authMiddleware = $middlewares | Where-Object { (Get-Path $_ 'metadata.name') -eq 'aerie-auth' } | Select-Object -First 1
    if ($authMode -eq 'none') {
        if ($null -eq $authMiddleware) {
            Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth absent' -Status 'Pass' -Detail 'AUTH_MODE=none, and nothing of the wall is rendered'
        }
        else {
            Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth absent' -Status 'Fail' -Detail 'AUTH_MODE=none but the Middleware still exists - the rollback did not take, or Flux has not reconciled it away'
        }
    }
    elseif ($null -eq $authMiddleware) {
        Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth' -Status 'Fail' -Detail "AUTH_MODE=$authMode but no Middleware aerie-auth in the aerie namespace - every annotated route is answering 500"
    }
    else {
        # The address is checked in full rather than for "not empty": a
        # forwardAuth pointed at the wrong path answers 200 to everything the
        # app serves, which is a wall that is up and lets everyone through.
        $expectedAddress = "http://api.aerie.svc.cluster.local:8080/api/auth/verify"
        $actualAddress = [string](Get-Path $authMiddleware 'spec.forwardAuth.address')
        $responseHeaders = @(Get-Path $authMiddleware 'spec.forwardAuth.authResponseHeaders')
        if ($actualAddress -ne $expectedAddress) {
            Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth' -Status 'Fail' -Detail "forwardAuth.address is '$actualAddress', want '$expectedAddress'"
        }
        elseif (-not ($responseHeaders -contains 'X-Aerie-Grant')) {
            Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth' -Status 'Fail' -Detail "authResponseHeaders is '$($responseHeaders -join ', ')' - X-Aerie-Grant is missing, so nothing downstream can tell who is calling"
        }
        else {
            Add-Check -Step 'auth.5' -Name 'Middleware aerie-auth' -Status 'Pass' -Detail "forwardAuth to $actualAddress, returning $($responseHeaders -join ', ')"
        }
    }

    # --- the admin flag: the axis the wall does not cover ------------------
    # docs/auth-architecture.md, "The admin flag". Nothing is rendered for this
    # one - it is a single env var on the api container - so what is checkable
    # from here is that the value is one the chart knows, and that it was not
    # asked for on an install that cannot honour it.
    if ($adminMode -notin @('none', 'enforced')) {
        Add-Check -Step 'auth.5' -Name 'ADMIN_MODE is one of none/enforced' -Status 'Fail' -Detail "ADMIN_MODE='$adminMode' in aerie-cluster-config is not a value the chart knows - middleware-auth.yaml fails the render rather than half-applying, so this presents as a stuck HelmRelease. 'off' and 'on' are the likeliest wrong answers and are YAML 1.1 booleans, which is why they are not the vocabulary"
    }
    elseif ($adminMode -eq 'enforced' -and $authMode -eq 'none') {
        # Not a chart error - it renders, and AdminGate reads both switches, so
        # what actually happens is that enforcement stays dormant. An operator
        # who asked for it and did not get it is exactly the half-state these
        # checks exist to name.
        Add-Check -Step 'auth.5' -Name 'ADMIN_MODE agrees with AUTH_MODE' -Status 'Fail' -Detail 'ADMIN_MODE=enforced but AUTH_MODE=none - there is no wall, so no request carries an identity and AdminGate stays dormant. The admin app is open to the LAN despite the setting; set AUTH_MODE to canary or full, or set ADMIN_MODE back to none'
    }
    elseif ($adminMode -eq 'enforced') {
        Add-Check -Step 'auth.5' -Name 'ADMIN_MODE agrees with AUTH_MODE' -Status 'Pass' -Detail "ADMIN_MODE=enforced with AUTH_MODE=$authMode - the admin app and its verbs want a person carrying IsAdmin"
    }
    else {
        Add-Check -Step 'auth.5' -Name 'ADMIN_MODE agrees with AUTH_MODE' -Status 'Pass' -Detail 'ADMIN_MODE=none - every enrolled device can do everything, which is the pre-flag posture and the rollback'
    }

    # Which Ingresses carry the annotation is the whole difference between the
    # three modes, and the <namespace>-<name>@kubernetescrd form is unforgiving
    # - a bare name is silently not found and the route serves unauthenticated,
    # which looks exactly like success from outside.
    $expectedAnnotation = 'aerie-aerie-auth@kubernetescrd'
    # Assigned inside the branches rather than from the switch's own output:
    # a branch whose value is @() emits nothing into the pipeline, so
    # `$x = switch (...) { 'none' { @() } }` leaves $x as $null and none would
    # take the unrecognised-mode path below.
    $shouldCarry = $null
    switch ($authMode) {
        'none' { $shouldCarry = @() }
        'canary' { $shouldCarry = @('docs-canary') }
        'full' { $shouldCarry = @('home', 'kiosk') }
    }
    if ($null -eq $shouldCarry) {
        Add-Check -Step 'auth.5' -Name 'AUTH_MODE is one of none/canary/full' -Status 'Fail' -Detail "AUTH_MODE='$authMode' in aerie-cluster-config is not a mode the chart knows - the chart fails its render rather than half-applying, so this presents as a stuck HelmRelease. 'off' and 'on' are the retired vocabulary and are the likeliest value to find here: they are YAML 1.1 booleans, which is why they were retired"
        $shouldCarry = @()
    }
    foreach ($ingress in $ingresses) {
        $name = [string](Get-Path $ingress 'metadata.name')
        # Two Get-Fields rather than one dotted Get-Path: the annotation key
        # has dots of its own, which Get-Path's own docstring warns it splits.
        $annotation = [string](Get-Field (Get-Path $ingress 'metadata.annotations') 'traefik.ingress.kubernetes.io/router.middlewares')
        $carries = @($annotation -split ',' | ForEach-Object { $_.Trim() })
        if ($shouldCarry -contains $name) {
            if ($carries -contains $expectedAnnotation) {
                Add-Check -Step 'auth.5' -Name "Ingress $name is behind the wall" -Status 'Pass' -Detail "router.middlewares: $annotation"
            }
            else {
                Add-Check -Step 'auth.5' -Name "Ingress $name is behind the wall" -Status 'Fail' -Detail "router.middlewares is '$annotation' - '$expectedAnnotation' is missing, so this route serves unauthenticated"
            }
        }
        elseif ($carries -contains $expectedAnnotation) {
            Add-Check -Step 'auth.5' -Name "Ingress $name is not behind the wall" -Status 'Fail' -Detail "AUTH_MODE=$authMode, but router.middlewares is '$annotation' - this route is walled and should not be"
        }
        else {
            Add-Check -Step 'auth.5' -Name "Ingress $name is not behind the wall" -Status 'Pass' -Detail "AUTH_MODE=$authMode, no auth annotation"
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
        #
        # This is quietly an auth check as well, and is left unconditional on
        # purpose. This probe carries no cookie, so at
        # AUTH_MODE=full a 200 here is AuthGate's /health/ready exemption being
        # honoured through an annotated route. Gating it instead fails every
        # pod's readiness probe and the Deployment never becomes available - a
        # total outage whose cause looks nothing like auth, which is why it is
        # first on the allow-list in docs/auth-architecture.md and asserted here in
        # every mode rather than only in one.
        $homeResponse = $responses["home.$domain"]
        if (-not $homeResponse.Connected) {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Fail' -Detail "$($homeResponse.Error)"
        }
        elseif ($homeResponse.StatusCode -eq 200) {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Pass' -Detail "200 on /health/ready$(if ($authMode -eq 'full') { ', through the wall - the probe exemption holds' })"
        }
        else {
            Add-Check -Step '5b.14' -Name "home.$domain answers" -Status 'Fail' -Detail "status=$($homeResponse.StatusCode)$(if ($authMode -eq 'full' -and $homeResponse.StatusCode -in @(401, 302)) { ' - /health/ready is being challenged, which fails every readiness probe in the cluster' })"
        }

        # kiosk: the root path, rewritten server-side by middleware-kiosk.yaml
        # (5b.9) before it reaches api - a 200 alone doesn't prove the
        # rewrite fired, since the unrewritten API landing page also answers
        # 200, so this checks the body for the dashboard SPA's own <title>
        # rather than the status code.
        #
        # Two-sided (docs/auth-architecture.md), because at
        # AUTH_MODE=full this route is walled and this probe carries no cookie:
        # the correct answer there is a refusal, not the dashboard, and
        # asserting 200 unconditionally would fail the gate on a deploy that
        # did exactly what it was asked to. The rewrite still runs for enrolled
        # tablets - auth is named first in the middleware list so the challenge
        # is on the / the tablet asked for rather than the rewritten path - and
        # what that looks like on a tablet is on the hand list, which is where
        # a GeckoView cookie surviving a reboot has to be confirmed anyway.
        $kioskResponse = $responses["kiosk.$domain"]
        $kioskWalled = $authMode -eq 'full'
        $kioskLocation = [string]$kioskResponse.Headers['Location']
        if (-not $kioskResponse.Connected) {
            Add-Check -Step '5b.14' -Name "kiosk.$domain answers" -Status 'Fail' -Detail "$($kioskResponse.Error)"
        }
        elseif ($kioskWalled) {
            # No Accept header on this probe, so the refusal is the 401 form.
            # Either answer proves the annotation resolved; both are recorded
            # because a 302 to anywhere but the sign-in shell is a different
            # bug, and the comma-separated middleware list is new here - kiosk
            # is the one route carrying two, and a list Traefik cannot resolve
            # in full serves unrewritten, unauthenticated, or 500s.
            if ($kioskResponse.StatusCode -eq 401) {
                Add-Check -Step 'auth.6' -Name "kiosk.$domain is behind the wall" -Status 'Pass' -Detail '401 to a request with no grant'
            }
            elseif ($kioskResponse.StatusCode -eq 302 -and $kioskLocation -like '/apps/auth/*') {
                Add-Check -Step 'auth.6' -Name "kiosk.$domain is behind the wall" -Status 'Pass' -Detail "302 to $kioskLocation"
            }
            elseif ($kioskResponse.StatusCode -eq 200 -and $kioskResponse.Body -match '<title>\s*Aerie Dashboard') {
                Add-Check -Step 'auth.6' -Name "kiosk.$domain is behind the wall" -Status 'Fail' -Detail 'AUTH_MODE=full but the dashboard was served to a request with no grant - the aerie-auth half of this route''s middleware list is not resolving, and every tablet-shaped device on the LAN is unwalled'
            }
            else {
                Add-Check -Step 'auth.6' -Name "kiosk.$domain is behind the wall" -Status 'Fail' -Detail "AUTH_MODE=full but this answered $($kioskResponse.StatusCode)$(if ($kioskLocation) { " to $kioskLocation" }) rather than a refusal"
            }
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

        # --- the wall, from outside (docs/auth-architecture.md) ---------------
        # The checks above prove the objects exist and say the right thing.
        # These prove Traefik acts on them, which is not the same claim: the
        # canary route wins over home's PathPrefix('/') only because Traefik
        # ranks routers by rule length, and if that ever goes the other way
        # the request is served *unauthenticated* - a failure that a clean
        # `helm diff` and every object check above would call a pass. An
        # actual 302 from an un-enrolled client is the only thing that proves
        # it, which is why this is here and not only up there.
        $docsResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "home.$domain" -Path '/apps/docs/' -Accept 'text/html'
        $docsWalled = $authMode -in @('canary', 'full')
        $docsLocation = [string]$docsResponse.Headers['Location']
        if (-not $docsResponse.Connected) {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ is behind the wall" -Status 'Fail' -Detail "$($docsResponse.Error)"
        }
        elseif ($docsWalled -and $docsResponse.StatusCode -eq 302 -and $docsLocation -like '/apps/auth/*') {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ is behind the wall" -Status 'Pass' -Detail "302 to $docsLocation"
        }
        elseif ($docsWalled -and $docsResponse.StatusCode -eq 302) {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ is behind the wall" -Status 'Fail' -Detail "302, but to '$docsLocation' rather than the sign-in shell"
        }
        elseif ($docsWalled) {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ is behind the wall" -Status 'Fail' -Detail "AUTH_MODE=$authMode but this answered $($docsResponse.StatusCode) unauthenticated - either the annotation is not resolving (the <namespace>-<name>@kubernetescrd form) or home's PathPrefix('/') router is outranking the canary's"
        }
        elseif ($docsResponse.StatusCode -eq 200) {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ serves unwalled" -Status 'Pass' -Detail "200, and AUTH_MODE=none"
        }
        else {
            Add-Check -Step 'auth.5' -Name "home.$domain/apps/docs/ serves unwalled" -Status 'Fail' -Detail "AUTH_MODE=none but this answered $($docsResponse.StatusCode)"
        }

        # The containment, and through phase 5 the assertion that matters most:
        # the canary walls one app and leaves the house alone. A 302 here at
        # AUTH_MODE=canary means the wall is wider than it was asked to be and
        # the operator's own way back in is behind the thing being tested.
        $homeRootResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "home.$domain" -Path '/' -Accept 'text/html'
        $homeWalled = $authMode -eq 'full'
        if (-not $homeRootResponse.Connected) {
            Add-Check -Step 'auth.5' -Name "home.$domain/ blast radius" -Status 'Fail' -Detail "$($homeRootResponse.Error)"
        }
        elseif ($homeWalled) {
            if ($homeRootResponse.StatusCode -eq 302 -and ([string]$homeRootResponse.Headers['Location']) -like '/apps/auth/*') {
                Add-Check -Step 'auth.5' -Name "home.$domain/ is behind the wall" -Status 'Pass' -Detail "302 to $([string]$homeRootResponse.Headers['Location'])"
            }
            else {
                Add-Check -Step 'auth.5' -Name "home.$domain/ is behind the wall" -Status 'Fail' -Detail "AUTH_MODE=full but / answered $($homeRootResponse.StatusCode) unauthenticated"
            }
        }
        elseif ($homeRootResponse.StatusCode -in @(200, 302) -and ([string]$homeRootResponse.Headers['Location']) -notlike '/apps/auth/*') {
            # 302 is also a pass here and is not the wall: Program.cs's
            # RewriteOptions bounce / to /apps/ before anything else does.
            Add-Check -Step 'auth.5' -Name "home.$domain/ is not behind the wall" -Status 'Pass' -Detail "$($homeRootResponse.StatusCode), AUTH_MODE=$authMode - the rest of the house is untouched"
        }
        else {
            Add-Check -Step 'auth.5' -Name "home.$domain/ is not behind the wall" -Status 'Fail' -Detail "AUTH_MODE=$authMode but / answered $($homeRootResponse.StatusCode) to $([string]$homeRootResponse.Headers['Location']) - the wall is wider than the canary, and the way back in is behind it"
        }

        # The other direction, and the one every check above is blind to: an
        # enrolled device is *served*. Every probe here is credential-free by
        # default, so a wall that refused everyone equally - a gate that fell
        # closed on a bad AuthGate lookup, an empty grant table, a cookie name
        # the pod and the browser disagree about - would pass all of them.
        # Only a request carrying a live grant separates "the wall works" from
        # "nothing gets in".
        #
        # Nothing to assert below full: at none and canary home/ is served
        # to everyone, which the check above already proved.
        #
        # -GrantToken is optional and its absence is a Warn rather than a Fail
        # (see the parameter's own help): the refusal half is the half that can
        # lock the house out, and it is asserted either way, so a run without a
        # credential is still a useful gate - just one that has proven half of
        # what the wall claims.
        if ($authMode -eq 'full' -and -not $GrantToken) {
            Add-Check -Step 'auth.6' -Name "home.$domain/ serves an enrolled device" -Status 'Warn' -Detail 'no -GrantToken passed, so only the refusal half of the wall was proven here - the serving half is on the hand list in docs/auth-architecture.md'
        }
        elseif ($authMode -eq 'full') {
            # __Secure-aerie_grant is AuthOptions.CookieName's default and the
            # chart overrides it nowhere, so it is the name in the cluster. If
            # that ever stops being true this check starts failing as though
            # the token were wrong, which is worth knowing when reading it.
            $enrolledResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "home.$domain" -Path '/' -Accept 'text/html' -Cookie "__Secure-aerie_grant=$GrantToken"
            $enrolledLocation = [string]$enrolledResponse.Headers['Location']
            if (-not $enrolledResponse.Connected) {
                Add-Check -Step 'auth.6' -Name "home.$domain/ serves an enrolled device" -Status 'Fail' -Detail "$($enrolledResponse.Error)"
            }
            elseif ($enrolledLocation -like '/apps/auth/*') {
                Add-Check -Step 'auth.6' -Name "home.$domain/ serves an enrolled device" -Status 'Fail' -Detail "the wall refused a request carrying -GrantToken, redirecting to $enrolledLocation - the grant is revoked or expired, or the cookie name in the pod is not __Secure-aerie_grant"
            }
            elseif ($enrolledResponse.StatusCode -in @(200, 302)) {
                # Same as the unwalled branch above: Program.cs's RewriteOptions
                # bounce / to /apps/ before the wall is reached, so a 302 that
                # is not to the sign-in shell is the app answering, not a
                # refusal.
                Add-Check -Step 'auth.6' -Name "home.$domain/ serves an enrolled device" -Status 'Pass' -Detail "$($enrolledResponse.StatusCode)$(if ($enrolledLocation) { " to $enrolledLocation" }) - the wall admits a grant as well as refusing without one"
            }
            else {
                Add-Check -Step 'auth.6' -Name "home.$domain/ serves an enrolled device" -Status 'Fail' -Detail "$($enrolledResponse.StatusCode) to a request carrying a grant - not a refusal, but not the app either"
            }
        }

        # No Accept header, so this is what the docs app's own fetch() sees. A
        # 302 here is the failure even though it is a refusal: the browser
        # follows it, the sign-in shell's HTML comes back 200, and the caller
        # reports a JSON parse error with nothing in it about authentication.
        $docsApiResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "home.$domain" -Path '/api/docs'
        if (-not $docsApiResponse.Connected) {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs refuses a fetch visibly" -Status 'Fail' -Detail "$($docsApiResponse.Error)"
        }
        elseif ($docsWalled -and $docsApiResponse.StatusCode -eq 401) {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs refuses a fetch visibly" -Status 'Pass' -Detail '401, which a fetch can actually see'
        }
        elseif ($docsWalled -and $docsApiResponse.StatusCode -eq 302) {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs refuses a fetch visibly" -Status 'Fail' -Detail '302 rather than 401 - invisible to fetch, and it surfaces later as a JSON parse error somewhere unrelated'
        }
        elseif ($docsWalled) {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs refuses a fetch visibly" -Status 'Fail' -Detail "AUTH_MODE=$authMode but this answered $($docsApiResponse.StatusCode) unauthenticated"
        }
        elseif ($docsApiResponse.StatusCode -eq 200) {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs serves unwalled" -Status 'Pass' -Detail '200, and AUTH_MODE=none'
        }
        else {
            Add-Check -Step 'auth.5' -Name "home.$domain/api/docs serves unwalled" -Status 'Fail' -Detail "AUTH_MODE=none but this answered $($docsApiResponse.StatusCode)"
        }

        # The exemption that costs the most to get wrong. Sonos speakers fetch
        # the stream themselves and cannot hold a cookie, so a gated /media
        # stops all music with no error anywhere that mentions authentication.
        # A 404 is the expected answer to the prefix itself and is a pass - the
        # claim being checked is "not challenged", not "serves a file".
        $mediaResponse = Invoke-HttpsGet -IPAddress $ingressVip -ServerName "home.$domain" -Path '/media/'
        if (-not $mediaResponse.Connected) {
            Add-Check -Step 'auth.5' -Name '/media is exempt from the wall' -Status 'Fail' -Detail "$($mediaResponse.Error)"
        }
        elseif ($mediaResponse.StatusCode -in @(401, 302)) {
            Add-Check -Step 'auth.5' -Name '/media is exempt from the wall' -Status 'Fail' -Detail "$($mediaResponse.StatusCode) - the media prefix is being challenged, which stops every Sonos speaker in the house"
        }
        else {
            Add-Check -Step 'auth.5' -Name '/media is exempt from the wall' -Status 'Pass' -Detail "$($mediaResponse.StatusCode), not a challenge"
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
