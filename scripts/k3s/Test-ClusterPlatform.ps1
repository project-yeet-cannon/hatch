<#
.SYNOPSIS
    Asserts every *Exit* criterion of the cluster plan Phase 3b in one run, and
    exits non-zero if any of them is not met. "Phase 3 is done" as a command
    rather than as a memory.

.DESCRIPTION
    Phase 3b.13. Steps 1-12 each carry an *Exit* line - a command whose output
    proves that step landed. Run one at a time, by hand, months apart, they are
    twelve things to remember and a report nobody can reproduce. This runs all
    of them, prints one table, and returns an exit code.

    It also doubles as the smoke test after a node rebuild or a cluster
    restore, which is the reason it reads its expectations from the cluster and
    the repository rather than taking them as parameters: there is nothing to
    remember to pass, so a run six months from now checks the same things this
    one does.

    Three properties worth stating, because they are what make this a gate
    rather than a script that prints things:

    **It does not stop at the first failure.** Every other script in scripts/
    throws on the first problem, correctly - their next action writes to a disk
    or to etcd, so continuing past a surprise is how a wrong assumption becomes
    a wrong filesystem. Nothing here writes anything. A gate that stops at the
    first failure costs one dispatch per problem, which on a twelve-step phase
    is how a bad afternoon becomes a bad week. Preflight still throws: with no
    SSH channel there is nothing to check at all.

    **A check it cannot evaluate is a failure, not a skip.** An absent object,
    an unparseable response and a broken query are all "this was not proven",
    and a gate that reports unproven as anything other than failure is a gate
    that can be satisfied by a cluster that is off.

    **Where a check is made from is part of the check.** The three ingress
    criteria - the VIP answers, Traefik answers, the certificate is the
    production wildcard - are evaluated from *this machine*, over the LAN, not
    over the SSH channel from a node. A node asked whether the VIP answers can
    say yes about its own loopback while every client on the LAN sees nothing;
    that is precisely the kube-vip failure 3b.9 documents. The rest is read
    through `sudo k3s kubectl` on -IPAddress, since it is cluster state and any
    server sees the same etcd.

    Stages:
      1. Preflight  - the SSH key resolves, the client is present, the node
                      answers 22, and the two committed maps parse.
      2. Probe      - one round trip collects every object the checks below
                      reason about.
      3. Cluster    - steps 3b.1 and 3b.3-3b.12, evaluated against that
                      snapshot.
      4. Nodes      - 3b.2's mount, and which node actually holds the VIP,
                      asked of every node the cluster reports rather than of
                      the one this run was pointed at.
      5. LAN        - 3b.8, 3b.9 and 3b.10's exit criteria, from here.
      6. Portability- the base domain and the VIP appear in deploy/ only as
                      ${...} substitutions, per docs/ethos.md. This is the one
                      check that needs both halves at once - the repository and
                      the values this installation actually uses - which is why
                      it lives here and not in ci.yml, where the values are
                      deliberately absent.
      7. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here is cluster
    state. See docs/secrets-architecture.md on why this is node 1's address
    today and why that is a known, accepted gap until Phase 7.

.PARAMETER DataDiskSizeGB
    What Phase 1 attached, so 3b.2's mount can be recognised as the data disk
    rather than as the root filesystem. A *description* of the hardware, not a
    choice - the same parameter Initialize-NodeStorage.ps1 takes, and it has to
    match what was passed there.

.EXAMPLE
    .\Test-ClusterPlatform.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$MapPath = (Join-Path $PSScriptRoot 'cluster-config.json'),
    [string]$ParametersPath = (Join-Path $PSScriptRoot '..\secrets\parameters.json'),
    [string]$DeployPath = (Join-Path $PSScriptRoot '..\..\deploy'),

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [int]$DataDiskSizeGB = 200,

    [ValidateRange(1, 50)]
    [int]$SizeTolerancePercent = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# The mount 3b.2 creates and 3b.11 configures Longhorn to use. Named once:
# these two steps agreeing is the whole point of the pair, so a literal in two
# places is a way for them to stop agreeing.
$MountPoint = '/var/lib/longhorn'

# Every HelmRelease this phase installs, as (namespace, name). Enumerated
# rather than derived from "whatever is in the cluster": a release that was
# never created is the failure most worth catching, and a set built from the
# cluster's own answer can never notice something missing from it.
$ExpectedReleases = @(
    @{ Namespace = 'external-secrets'; Name = 'external-secrets'; Step = '3b.4' }
    @{ Namespace = 'cert-manager'; Name = 'cert-manager'; Step = '3b.7' }
    @{ Namespace = 'kube-system'; Name = 'kube-vip-cloud-provider'; Step = '3b.8' }
    @{ Namespace = 'kube-system'; Name = 'kube-vip'; Step = '3b.8' }
    @{ Namespace = 'longhorn-system'; Name = 'longhorn'; Step = '3b.11' }
    @{ Namespace = 'cnpg-system'; Name = 'cloudnative-pg'; Step = '3b.12' }
)

# The CRDs each controller has to have registered before the objects under
# config/ can even be applied - the two-layer split's whole premise (3b.3).
$ExpectedCrds = @(
    @{ Name = 'externalsecrets.external-secrets.io'; Step = '3b.4'; Why = 'External Secrets Operator' }
    @{ Name = 'clustersecretstores.external-secrets.io'; Step = '3b.4'; Why = 'External Secrets Operator' }
    @{ Name = 'certificates.cert-manager.io'; Step = '3b.7'; Why = 'cert-manager' }
    @{ Name = 'clusterissuers.cert-manager.io'; Step = '3b.7'; Why = 'cert-manager' }
    @{ Name = 'volumes.longhorn.io'; Step = '3b.11'; Why = 'Longhorn' }
    @{ Name = 'clusters.postgresql.cnpg.io'; Step = '3b.12'; Why = 'CloudNativePG' }
)

# The two flags 3a.4 exists to justify, read back off the running Deployment
# rather than off the manifest. The manifest sets them as chart *values*; what
# this proves is that they arrived as process arguments, which is the only
# thing that distinguishes "configured" from "typed into a key the chart never
# read" - and the symptom of the latter is indistinguishable from the
# split-horizon failure they were set to fix.
$ExpectedCertManagerArgs = @(
    '--dns01-recursive-nameservers-only'
    '--dns01-recursive-nameservers='
)

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
        link is missing.

    .DESCRIPTION
        Kubernetes objects are deep and half-optional - `.status.loadBalancer`
        exists on a Service that has no address, and `.ingress` does not - so
        every read here would otherwise be four nested null checks. This makes
        an absent path indistinguishable from an absent value, which is right
        for a gate: both mean "not proven".
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

    .DESCRIPTION
        Every call site wraps this in @( ), and has to: PowerShell unrolls a
        collection written to the pipeline, so a list holding exactly one item
        arrives at the caller as a scalar and its `.Count` then fails under
        Set-StrictMode - on a phase that installs one of most things. The
        comma operator is the other fix for that and is deliberately *not*
        used here, because the two do not compose: `,@()` returned into an
        `@( )` yields a one-element array holding an empty one, so a cluster
        with no ExternalSecrets would read as a cluster with one.
    #>
    param([Parameter(Mandatory)][AllowNull()]$List)
    return @(Get-Field $List 'items' | Where-Object { $_ })
}

function Get-MapValue {
    <#
    .SYNOPSIS
        Reads one key out of a Kubernetes string map - labels, annotations,
        ConfigMap data - by its literal name.

    .DESCRIPTION
        Get-Path cannot reach these: annotation keys contain dots and slashes
        (`kube-vip.io/loadbalancerIPs`), which a dotted path has no way to
        distinguish from a nesting level. Escaping the dots would only move the
        problem, since the escape has to be understood by both this and
        PowerShell's own property lookup - so the map is searched by name
        instead, which has no ambiguity in it at all.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$Map,
        [Parameter(Mandatory)][string]$Key
    )
    if ($null -eq $Map) { return $null }
    $property = $Map.PSObject.Properties | Where-Object { $_.Name -eq $Key } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function Get-ReadyCondition {
    <#
    .SYNOPSIS
        The `Ready` entry of an object's status.conditions, or $null.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Object)
    $conditions = @(Get-Path $Object 'status.conditions' | Where-Object { $_ })
    return ($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Ready' } | Select-Object -First 1)
}

function Format-Condition {
    <#
    .SYNOPSIS
        Renders a condition as `Reason: message`, trimmed to one line.

    .DESCRIPTION
        The reason and message are the entire diagnostic value of a failed
        check - "not Ready" names a symptom this script already knew about,
        while "SecretSyncError: unable to find secret" names the fix. Trimmed
        because controller messages routinely run to several hundred
        characters and this lands in a table.
    #>
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
    <#
    .SYNOPSIS
        Pulls one '--- name' section out of the combined probe output.
    #>
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
    <#
    .SYNOPSIS
        Parses one probe section as JSON, returning $null rather than throwing
        when the section is empty or is not JSON at all.

    .DESCRIPTION
        Every JSON-producing command in the probe falls back to `echo {}` when
        its type is not registered, so "the CRD does not exist yet" arrives
        here as an object with no items rather than as an error - which is
        what lets one probe cover a half-built cluster without the whole run
        dying on the first missing kind.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$Name
    )
    $section = Get-ProbeSection -Output $Output -Name $Name
    if ([string]::IsNullOrWhiteSpace($section)) { return $null }
    try { return $section | ConvertFrom-Json }
    catch { return $null }
}

function Format-Size {
    param([Parameter(Mandatory)][int64]$Bytes)
    if ($Bytes -le 0) { return '0' }
    return '{0:N1} GiB' -f ($Bytes / 1GB)
}

function Format-Revision {
    <#
    .SYNOPSIS
        Shortens a Flux source revision to something a table column can hold.
    #>
    param([AllowNull()][string]$Revision)
    if ([string]::IsNullOrWhiteSpace($Revision)) { return '(none)' }
    # `main@sha1:6b59a9...` - the branch is the half that identifies it to a
    # human, and seven hex digits is what git itself considers enough.
    if ($Revision -match '^(?<branch>[^@]+)@sha\d+:(?<sha>[0-9a-f]{7})') {
        return "$($Matches['branch'])@$($Matches['sha'])"
    }
    if ($Revision.Length -gt 24) { return $Revision.Substring(0, 23) + '…' }
    return $Revision
}

function Get-LonghornSettingValues {
    <#
    .SYNOPSIS
        Splits a Longhorn Setting's value into one entry per data engine,
        covering both shapes the field takes.

    .DESCRIPTION
        Longhorn made its data-engine-specific settings - default-replica-count
        among them - hold a JSON object keyed by engine rather than a bare
        string, so a chart that writes `defaultReplicaCount: "2"` reads back as
        {"v1":"2","v2":"2"} on 1.11. Settings that are not engine-specific are
        still plain scalars, and both forms are current in the same cluster.

        This matters more than a format detail, because the check above it is a
        *read-back*: comparing the raw string to the expected count reports a
        correctly configured cluster as broken, which is the one failure mode a
        gate cannot have. A gate that cries wolf is a gate that gets ignored,
        and then the real wrong-replica-count goes with it.

        Returns one object per engine (Engine = $null for the scalar form), or
        an empty array for a value that is neither - which the caller reports as
        a failure rather than as a pass, since an unrecognised shape means the
        setting was not read at all.
    #>
    param([AllowNull()][AllowEmptyString()][string]$Value)

    $trimmed = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return @() }

    if (-not $trimmed.StartsWith('{')) {
        return @([pscustomobject]@{ Engine = $null; Value = $trimmed })
    }

    $parsed = $null
    try { $parsed = $trimmed | ConvertFrom-Json }
    catch { return @() }
    if ($null -eq $parsed) { return @() }

    $entries = New-Object Collections.Generic.List[psobject]
    foreach ($property in $parsed.PSObject.Properties) {
        $entries.Add([pscustomobject]@{ Engine = $property.Name; Value = [string]$property.Value })
    }
    # Plain `@( )`, never `,@( )` - see Get-Items for why the comma operator
    # cannot be used here.
    return @($entries)
}

$script:Checks = New-Object Collections.Generic.List[psobject]
function Add-Check {
    <#
    .SYNOPSIS
        Records one result. Pass, Fail or Warn - there is deliberately no
        'Skip'.

    .DESCRIPTION
        A check that could not be evaluated is a Fail: the cluster has not
        demonstrated the property, and a gate that distinguishes "false" from
        "unknown" in its exit code is a gate that passes on a cluster that is
        merely unreachable. Warn exists for the two things that are genuinely
        advisory - an ICMP reply, which a firewall may legitimately swallow
        while the service underneath is perfectly healthy, and drift that is
        expected to be repaired on the next reconciliation.
    #>
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
        The commonest shape in this file: the object exists and its Ready
        condition is True.
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
    # A suspended object keeps whatever condition it last had, forever, and
    # will happily report Ready=True about a reconciliation that stopped weeks
    # ago. Checked before the condition rather than after, because reading the
    # condition of a suspended object is reading history.
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

function Invoke-TlsProbe {
    <#
    .SYNOPSIS
        Opens one TLS connection, returns the certificate the server presented
        and the status line it answers a plain GET with.

    .DESCRIPTION
        3b.9 and 3b.10's exit criteria are `curl -k https://${INGRESS_VIP}` and
        `openssl s_client -connect ${INGRESS_VIP}:443 -servername
        home.${DOMAIN}`. Neither binary is on a Windows runner by default, and
        the interesting half of both is one connection's worth of information,
        so both are done here over one socket.

        -ServerName is sent as SNI and as the Host header while the connection
        is made to an *address*, which is deliberate and is the same thing
        `-servername` does: no DNS anywhere in this phase points at the VIP -
        that is Phase 7 - so a check that resolved the name would be testing
        the internal view of the zone rather than the cluster.

        Certificate validation is accepted unconditionally. The certificate is
        the subject of the check, not a precondition of it: a staging
        certificate, an expired one and Traefik's own self-signed fallback are
        each a specific diagnosis this run wants to report, and every one of
        them would abort the handshake under normal validation.
    #>
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$ServerName,
        [int]$Port = 443,
        [int]$TimeoutMs = 15000
    )

    $result = [pscustomobject]@{
        Connected   = $false
        Certificate = $null
        StatusLine  = $null
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

        # Parameter names deliberately not the delegate's own ($sender,
        # $errors): both are PowerShell automatic variables, and binding them
        # inside a scriptblock is a side effect nobody goes looking for.
        $accept = [Net.Security.RemoteCertificateValidationCallback] { param($channel, $peerCertificate, $chain, $policyErrors) return $true }
        $ssl = New-Object Net.Security.SslStream($client.GetStream(), $false, $accept)
        $ssl.ReadTimeout = $TimeoutMs
        $ssl.WriteTimeout = $TimeoutMs

        # TLS 1.3 is only an enum member on .NET Framework 4.8 and later, and
        # this runs on whatever Windows PowerShell 5.1 is sitting on. Asked
        # for rather than assumed, and its absence is not a failure - 1.2 is
        # what the handshake needs.
        $protocols = [Security.Authentication.SslProtocols]::Tls12
        try { $protocols = $protocols -bor [Security.Authentication.SslProtocols]::Tls13 } catch { }

        $ssl.AuthenticateAsClient($ServerName, $null, $protocols, $false)
        $result.Connected = $true
        if ($ssl.RemoteCertificate) {
            $result.Certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        }

        # Written by hand rather than through HttpWebRequest, which would need
        # ServicePointManager's certificate callback - process-wide state that
        # a verification script has no business leaving behind on a runner
        # that goes on to do other work.
        $writer = New-Object IO.StreamWriter($ssl, (New-Object Text.ASCIIEncoding))
        $writer.NewLine = "`r`n"
        $writer.AutoFlush = $true
        $writer.WriteLine('GET / HTTP/1.1')
        $writer.WriteLine("Host: $ServerName")
        $writer.WriteLine('User-Agent: aerie-test-cluster-platform')
        $writer.WriteLine('Connection: close')
        $writer.WriteLine()

        $reader = New-Object IO.StreamReader($ssl, [Text.Encoding]::ASCII)
        $result.StatusLine = $reader.ReadLine()
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

function Get-CertificateDnsNames {
    <#
    .SYNOPSIS
        The DNS names out of a certificate's subjectAltName extension.

    .DESCRIPTION
        Read out of the extension's DER rather than out of X509Extension's
        Format(), which is neither portable nor stable: on .NET Framework it
        decodes through CryptoAPI and renders `DNS Name=example.com` in the
        *host's display language*, and on .NET Core it commonly returns the
        raw hex instead - verified, and the reason this parses bytes. A check
        whose subject is a certificate cannot afford to answer "no names
        found" on a certificate that has them, because that reads exactly like
        the wrong certificate being served.

        subjectAltName is a SEQUENCE of GeneralNames; a dNSName is the
        context-specific primitive tag 0x82. Nothing else here is of interest,
        so the walk skips every other tag by length rather than decoding it.
        Format() is kept as a fallback for a shape this does not expect.
    #>
    param([Parameter(Mandatory)]$Certificate)
    $names = New-Object Collections.Generic.List[string]

    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne '2.5.29.17') { continue }

        $bytes = $extension.RawData
        if ($null -eq $bytes -or $bytes.Length -lt 2 -or $bytes[0] -ne 0x30) { continue }

        # Skip the outer SEQUENCE header, whose length may be short-form (one
        # byte) or long-form (a count of length bytes, high bit set).
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

    # Wrapped by the caller in @( ) for the reason Get-Items documents.
    return @($names)
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
        $failures.Add("Not found: '$DeployPath'. Stage 6 greps the committed tree, so it needs the checkout, not just the cluster.")
    }

    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
        $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey. This is the same key Phase 1 baked into the node.')
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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-cluster-gate'
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
        # The one place this file throws rather than recording a failure:
        # without a channel to the cluster there is nothing to be partially
        # right about, and a table of twelve identical "could not connect"
        # rows is noise wearing the shape of a report.
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    $map = Get-Content -Path $MapPath -Raw | ConvertFrom-Json
    $parameters = Get-Content -Path $ParametersPath -Raw | ConvertFrom-Json

    Write-Host "Cluster:  $IPAddress"
    Write-Host "Repo:     $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host 'Preflight OK. Nothing below writes to the cluster.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # Single quotes only, and no double quotes anywhere: Invoke-NodeSsh refuses
    # a command containing one, because Windows PowerShell 5.1 lets ssh.exe
    # strip it and run a subtly different script on the node.
    #
    # Every command that asks for a type falls back to `echo {}`, so a cluster
    # missing a CRD answers "no items" instead of killing the probe - which is
    # the difference between this reporting eleven results and one error.
    # `-o json` throughout rather than jsonpath or custom-columns: the parsing
    # happens here, where a shape that is not what was expected can be reported
    # as such instead of silently yielding an empty string. That is a rule with
    # teeth - the one line that broke it, a jsonpath read of Longhorn's replica
    # setting, is also the one whose output carried no trailing newline and so
    # arrived glued to the marker after it as `{"v1":"2","v2":"2"}--- end`.
    #
    # Hence `printf` rather than `echo` for the markers: the leading \n means a
    # command whose last line is unterminated can no longer swallow the section
    # boundary behind it. Get-ProbeSection trims, so the extra blank line
    # between sections costs nothing, and the next command added here cannot
    # re-arm the trap by forgetting.
    $probeScript = @(
        'printf ''\n--- nodes\n'''
        'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        # The source every Kustomization below is measured against. Ready is
        # reported against whatever revision a layer last applied, which is not
        # necessarily the one that is committed.
        'printf ''\n--- gitrepository\n'''
        'sudo k3s kubectl -n flux-system get gitrepositories.source.toolkit.fluxcd.io flux-system -o json 2>/dev/null || echo {}'
        'printf ''\n--- kustomizations\n'''
        'sudo k3s kubectl -n flux-system get kustomizations.kustomize.toolkit.fluxcd.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- helmreleases\n'''
        'sudo k3s kubectl get helmreleases.helm.toolkit.fluxcd.io -A -o json 2>/dev/null || echo {}'
        'printf ''\n--- crds\n'''
        'sudo k3s kubectl get crd -o name 2>/dev/null || true'
        'printf ''\n--- clustersecretstores\n'''
        'sudo k3s kubectl get clustersecretstores.external-secrets.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- externalsecrets\n'''
        'sudo k3s kubectl get externalsecrets.external-secrets.io -A -o json 2>/dev/null || echo {}'
        # Names only. Never `-o json` over Secrets: this output is printed to a
        # run log, and the one thing that must not reach a run log is the thing
        # the whole ESO design exists to keep out of git.
        'printf ''\n--- secretnames\n'''
        'sudo k3s kubectl get secret -A -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name --no-headers 2>/dev/null || true'
        'printf ''\n--- certmanagerdeploy\n'''
        'sudo k3s kubectl -n cert-manager get deployment cert-manager -o json 2>/dev/null || echo {}'
        'printf ''\n--- clusterissuers\n'''
        'sudo k3s kubectl get clusterissuers.cert-manager.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- certificates\n'''
        'sudo k3s kubectl get certificates.cert-manager.io -A -o json 2>/dev/null || echo {}'
        'printf ''\n--- kubevippool\n'''
        'sudo k3s kubectl -n kube-system get configmap kubevip -o json 2>/dev/null || echo {}'
        'printf ''\n--- traefiksvc\n'''
        'sudo k3s kubectl -n kube-system get service traefik -o json 2>/dev/null || echo {}'
        'printf ''\n--- helmchartconfig\n'''
        'sudo k3s kubectl -n kube-system get helmchartconfigs.helm.cattle.io traefik -o json 2>/dev/null || echo {}'
        'printf ''\n--- tlsstores\n'''
        'sudo k3s kubectl get tlsstores.traefik.io -A -o json 2>/dev/null || echo {}'
        'printf ''\n--- storageclasses\n'''
        'sudo k3s kubectl get storageclasses.storage.k8s.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- longhornnodes\n'''
        'sudo k3s kubectl -n longhorn-system get nodes.longhorn.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- longhornreplicas\n'''
        'sudo k3s kubectl -n longhorn-system get settings.longhorn.io default-replica-count -o json 2>/dev/null || echo {}'
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

    $nodeList = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'nodes'
    $clusterConfig = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'clusterconfig'
    $gitRepository = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'gitrepository'
    $kustomizations = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'kustomizations'))
    $helmReleases = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'helmreleases'))
    $crdNames = @((Get-ProbeSection -Output $probe.StdOut -Name 'crds') -split "`n" | ForEach-Object { $_.Trim() -replace '^customresourcedefinition\.apiextensions\.k8s\.io/', '' } | Where-Object { $_ })
    $secretStores = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'clustersecretstores'))
    $externalSecrets = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'externalsecrets'))
    $secretNames = @((Get-ProbeSection -Output $probe.StdOut -Name 'secretnames') -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $certManagerDeploy = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'certmanagerdeploy'
    $clusterIssuers = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'clusterissuers'))
    $certificates = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'certificates'))
    $kubeVipPool = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'kubevippool'
    $traefikService = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'traefiksvc'
    $helmChartConfig = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'helmchartconfig'
    $tlsStores = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'tlsstores'))
    $storageClasses = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'storageclasses'))
    $longhornNodes = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'longhornnodes'))
    $longhornReplicas = [string](Get-Path (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'longhornreplicas') 'value')

    # ---------------------------------------------------------------- #
    Write-Stage 'Cluster'
    # ---------------------------------------------------------------- #

    # --- The cluster itself. Not one of the twelve steps; the thing all
    # --- twelve are about. The cluster plan's Verification section asks for it
    # --- once per phase, and every check below is meaningless without it.
    $nodes = @(Get-Items $nodeList)
    if ($nodes.Count -eq 0) {
        Add-Check -Step 'cluster' -Name 'Nodes Ready' -Status 'Fail' -Detail 'the apiserver reported no nodes at all'
    }
    else {
        $notReady = @($nodes | Where-Object {
                $ready = @(Get-Path $_ 'status.conditions' | Where-Object { (Get-Field $_ 'type') -eq 'Ready' }) | Select-Object -First 1
                (Get-Field $ready 'status') -ne 'True'
            } | ForEach-Object { Get-Path $_ 'metadata.name' })
        if ($notReady.Count -gt 0) {
            Add-Check -Step 'cluster' -Name 'Nodes Ready' -Status 'Fail' -Detail "$($notReady.Count) of $($nodes.Count) not Ready: $($notReady -join ', ')"
        }
        else {
            Add-Check -Step 'cluster' -Name 'Nodes Ready' -Status 'Pass' -Detail "$($nodes.Count) node(s)"
        }
    }

    # --- 3b.1 ---------------------------------------------------------
    # Exit: `kubectl -n flux-system get cm aerie-cluster-config -o yaml` lists
    # all seven keys. The key list comes from the same committed map
    # Set-ClusterConfig.ps1 renders from, so adding a key there makes this gate
    # require it without either file being edited.
    $configData = Get-Field $clusterConfig 'data'
    $requiredKeys = @(Get-Field $map 'keys' | Where-Object { $_ } | Where-Object { (Get-Field $_ 'required') } | ForEach-Object { [string](Get-Field $_ 'key') })
    $configValues = @{}
    if ($null -eq $configData) {
        Add-Check -Step '3b.1' -Name 'aerie-cluster-config ConfigMap' -Status 'Fail' -Detail 'absent, or has no data - dispatch Provision 4. Every Kustomization under deploy/ substitutes from this object'
    }
    else {
        foreach ($property in $configData.PSObject.Properties) { $configValues[$property.Name] = [string]$property.Value }
        $missing = @($requiredKeys | Where-Object { -not $configValues.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($configValues[$_]) })
        if ($missing.Count -gt 0) {
            Add-Check -Step '3b.1' -Name 'aerie-cluster-config ConfigMap' -Status 'Fail' -Detail "missing or empty: $($missing -join ', '). An undefined token substitutes as the empty string rather than failing, so these are Ingresses with no host and a Service asking for the address ''"
        }
        else {
            Add-Check -Step '3b.1' -Name 'aerie-cluster-config ConfigMap' -Status 'Pass' -Detail "$($requiredKeys.Count) key(s): $($requiredKeys -join ', ')"
        }
    }

    # Read once here and used by six checks below. Absent means those checks
    # cannot be evaluated, and Add-Check's contract makes that a failure rather
    # than a silent pass.
    $domain = if ($configValues.ContainsKey('DOMAIN')) { $configValues['DOMAIN'] } else { $null }
    $ingressVip = if ($configValues.ContainsKey('INGRESS_VIP')) { $configValues['INGRESS_VIP'] } else { $null }
    $nodeInterface = if ($configValues.ContainsKey('NODE_INTERFACE')) { $configValues['NODE_INTERFACE'] } else { $null }
    $replicaCount = if ($configValues.ContainsKey('LONGHORN_REPLICA_COUNT')) { $configValues['LONGHORN_REPLICA_COUNT'] } else { $null }

    # --- 3b.3 ---------------------------------------------------------
    # The two-layer split. flux-system is Provision 3's, and is checked here
    # because the other two are reconciled *by* it: a stalled root is why a
    # perfectly correct commit is not in the cluster.
    foreach ($name in @('flux-system', 'infra-controllers', 'infra-config')) {
        $kustomization = $kustomizations | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        [void](Add-ObjectReadyCheck -Step '3b.3' -Name "Kustomization $name" -Object $kustomization `
                -MissingDetail 'not found in flux-system')
    }

    # Ready is reported against whatever revision a layer last *applied*, while
    # dependsOn is enforced against the revision the source currently holds. A
    # commit that has reached the GitRepository but not yet reached
    # infra-controllers therefore surfaces above as infra-config failing with
    # "dependency 'flux-system/infra-controllers' revision is not up to date" -
    # which reads like a broken dependency and is usually a roll-out in flight.
    # Without this line the operator debugs the wrong thing.
    #
    # It earns its place as a check rather than a diagnostic, though: 3b.3's
    # exit criterion is that the cluster matches the tree, and three Ready
    # Kustomizations all pinned to last week's commit satisfy every other
    # assertion in this file.
    $sourceRevision = [string](Get-Path $gitRepository 'status.artifact.revision')
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) {
        Add-Check -Step '3b.3' -Name 'Layers are at the committed revision' -Status 'Fail' `
            -Detail 'the flux-system GitRepository has no artifact, so Flux has never fetched the repository and the layers above are reconciling against nothing'
    }
    else {
        $behind = New-Object Collections.Generic.List[string]
        foreach ($name in @('flux-system', 'infra-controllers', 'infra-config')) {
            $kustomization = $kustomizations | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
            # A missing one already failed above; reporting it twice buries the
            # finding that matters under the one that follows from it.
            if ($null -eq $kustomization) { continue }
            $applied = [string](Get-Path $kustomization 'status.lastAppliedRevision')
            if ($applied -ne $sourceRevision) { $behind.Add("$name at $(Format-Revision $applied)") }
        }

        if ($behind.Count -eq 0) {
            Add-Check -Step '3b.3' -Name 'Layers are at the committed revision' -Status 'Pass' -Detail (Format-Revision $sourceRevision)
        }
        else {
            Add-Check -Step '3b.3' -Name 'Layers are at the committed revision' -Status 'Fail' `
                -Detail "the repository is at $(Format-Revision $sourceRevision); $($behind -join ', '). kustomize-controller reconciles on a source change rather than waiting for its interval, so this is a roll-out still in flight - re-run once it lands - unless the layer is also not Ready above, which is the case where the commit is being refused"
        }
    }

    # --- CRDs, per the step that installs each ------------------------
    foreach ($crd in $ExpectedCrds) {
        if ($crdNames -contains $crd.Name) {
            Add-Check -Step $crd.Step -Name "CRD $($crd.Name)" -Status 'Pass'
        }
        else {
            Add-Check -Step $crd.Step -Name "CRD $($crd.Name)" -Status 'Fail' -Detail "not registered - $($crd.Why) has not installed its CRDs, so every object under config/ that uses this type is unappliable"
        }
    }

    # --- HelmReleases, per the step that installs each -----------------
    foreach ($expected in $ExpectedReleases) {
        $release = $helmReleases | Where-Object {
            (Get-Path $_ 'metadata.name') -eq $expected.Name -and (Get-Path $_ 'metadata.namespace') -eq $expected.Namespace
        } | Select-Object -First 1
        [void](Add-ObjectReadyCheck -Step $expected.Step -Name "HelmRelease $($expected.Namespace)/$($expected.Name)" -Object $release)
    }

    # Anything Flux is managing that this file does not know about. A Warn, not
    # a failure: Phases 4-8 add releases, and a gate that fails on the next
    # phase's work is a gate that gets edited out. What it catches is the
    # opposite case - something installed by hand, which is the one thing that
    # cannot be rebuilt from this repository.
    $unexpectedReleases = @($helmReleases | Where-Object {
            $item = $_
            -not ($ExpectedReleases | Where-Object {
                    $_.Name -eq (Get-Path $item 'metadata.name') -and $_.Namespace -eq (Get-Path $item 'metadata.namespace')
                })
        } | ForEach-Object { "$(Get-Path $_ 'metadata.namespace')/$(Get-Path $_ 'metadata.name')" })
    if ($unexpectedReleases.Count -gt 0) {
        Add-Check -Step 'extra' -Name 'HelmReleases beyond Phase 3' -Status 'Warn' -Detail ($unexpectedReleases -join ', ')
    }

    # --- 3b.5 ---------------------------------------------------------
    # Exit: the store reports Ready=True. Which is cheap, and the manifest says
    # so at length: static credentials are validated by being *retrieved*,
    # never by calling AWS, so an expired key or a tree seeded into another
    # region both report Ready. The one thing Ready does prove that is worth
    # asserting separately is below it - that both secretRefs name a namespace.
    $store = $secretStores | Where-Object { (Get-Path $_ 'metadata.name') -eq 'aerie-secrets' } | Select-Object -First 1
    if (Add-ObjectReadyCheck -Step '3b.5' -Name 'ClusterSecretStore aerie-secrets' -Object $store) {
        $refs = @(
            @{ Label = 'accessKeyIDSecretRef'; Path = 'spec.provider.aws.auth.secretRef.accessKeyIDSecretRef.namespace' }
            @{ Label = 'secretAccessKeySecretRef'; Path = 'spec.provider.aws.auth.secretRef.secretAccessKeySecretRef.namespace' }
        )
        $unqualified = @($refs | Where-Object { [string]::IsNullOrWhiteSpace([string](Get-Path $store $_.Path)) } | ForEach-Object { $_.Label })
        if ($unqualified.Count -gt 0) {
            Add-Check -Step '3b.5' -Name 'Store credentials are namespace-qualified' -Status 'Fail' `
                -Detail "$($unqualified -join ', ') carries no namespace. On a ClusterSecretStore that means referent auth - resolve the Secret in each consuming ExternalSecret's own namespace - so ESO skips validation entirely and still reports Ready=True over a store that has never looked at a credential"
        }
        else {
            Add-Check -Step '3b.5' -Name 'Store credentials are namespace-qualified' -Status 'Pass' -Detail 'both refs name external-secrets, so Ready means the bootstrap Secret was actually read'
        }
    }

    # --- 3b.6 ---------------------------------------------------------
    # Exit: every ExternalSecret reports SecretSynced. The expected set comes
    # from parameters.json - the same file New-ExternalSecrets.ps1 generates
    # the manifests from - so a parameter that gained a `kubernetes` block and
    # never got a manifest fails here as a missing object rather than as
    # nothing at all.
    $expectedSecrets = @{}
    foreach ($parameter in @(Get-Field $parameters 'parameters' | Where-Object { $_ })) {
        $kubernetes = Get-Field $parameter 'kubernetes'
        if ($null -eq $kubernetes) { continue }
        # Only what this phase creates. Phases 4, 5 and 8 add their own, and
        # this gate is Phase 3's.
        if ([int](Get-Field $parameter 'phase') -ne 3) { continue }
        $key = "$(Get-Field $kubernetes 'namespace')/$(Get-Field $kubernetes 'secretName')"
        $expectedSecrets[$key] = $true
    }

    foreach ($key in @($expectedSecrets.Keys | Sort-Object)) {
        $parts = $key -split '/'
        $external = $externalSecrets | Where-Object {
            (Get-Path $_ 'metadata.namespace') -eq $parts[0] -and (Get-Path $_ 'spec.target.name') -eq $parts[1]
        } | Select-Object -First 1

        if ($null -eq $external) {
            Add-Check -Step '3b.6' -Name "ExternalSecret for $key" -Status 'Fail' `
                -Detail "parameters.json declares this Secret and nothing in the cluster syncs it. Regenerate with scripts/secrets/New-ExternalSecrets.ps1 and commit"
            continue
        }

        $condition = Get-ReadyCondition $external
        $reason = [string](Get-Field $condition 'reason')
        if ((Get-Field $condition 'status') -eq 'True' -and $reason -eq 'SecretSynced') {
            # Synced is ESO's claim. That the Secret exists is the cluster's,
            # and they can differ: a deleted Secret is recreated on the next
            # refreshInterval, not immediately, so the window is real.
            if ($secretNames -match "^\s*$([regex]::Escape($parts[0]))\s+$([regex]::Escape($parts[1]))\s*$") {
                Add-Check -Step '3b.6' -Name "ExternalSecret for $key" -Status 'Pass' -Detail 'SecretSynced, Secret present'
            }
            else {
                Add-Check -Step '3b.6' -Name "ExternalSecret for $key" -Status 'Fail' -Detail 'reports SecretSynced but no such Secret exists - it was deleted out from under ESO and has not been resynced yet'
            }
        }
        else {
            Add-Check -Step '3b.6' -Name "ExternalSecret for $key" -Status 'Fail' -Detail (Format-Condition $condition)
        }
    }

    # Anything syncing that this phase did not ask for, held to the same bar -
    # an ExternalSecret in SecretSyncError takes infra-config's Ready condition
    # down with it whether or not this gate expected the object.
    $unsynced = @($externalSecrets | Where-Object {
            $condition = Get-ReadyCondition $_
            (Get-Field $condition 'status') -ne 'True'
        } | ForEach-Object { "$(Get-Path $_ 'metadata.namespace')/$(Get-Path $_ 'metadata.name')" })
    if ($unsynced.Count -gt 0) {
        Add-Check -Step '3b.6' -Name 'No ExternalSecret is failing to sync' -Status 'Fail' -Detail ($unsynced -join ', ')
    }
    else {
        Add-Check -Step '3b.6' -Name 'No ExternalSecret is failing to sync' -Status 'Pass' -Detail "$($externalSecrets.Count) in the cluster"
    }

    # --- 3b.7 ---------------------------------------------------------
    # The flags, off the running Deployment. See $ExpectedCertManagerArgs.
    $containers = @(Get-Path $certManagerDeploy 'spec.template.spec.containers' | Where-Object { $_ })
    $controller = $containers | Where-Object { (Get-Field $_ 'name') -eq 'cert-manager-controller' } | Select-Object -First 1
    if ($null -eq $controller) { $controller = $containers | Select-Object -First 1 }
    # Not $args - that is an automatic variable, and shadowing it inside a
    # script that also dot-sources a library is the kind of thing that works
    # until something in that library reads it.
    $controllerArgs = @(Get-Field $controller 'args' | Where-Object { $_ })
    if ($controllerArgs.Count -eq 0) {
        Add-Check -Step '3b.7' -Name 'DNS-01 uses public resolvers' -Status 'Fail' -Detail 'could not read the cert-manager Deployment args'
    }
    else {
        $missingArgs = @($ExpectedCertManagerArgs | Where-Object { $flag = $_; -not ($controllerArgs | Where-Object { $_.StartsWith($flag) }) })
        if ($missingArgs.Count -gt 0) {
            Add-Check -Step '3b.7' -Name 'DNS-01 uses public resolvers' -Status 'Fail' `
                -Detail "$($missingArgs -join ', ') is not on the controller's command line. The propagation self-check will resolve through CoreDNS to pfSense, which answers authoritatively for the internal view of the zone and never returns the public TXT - reported as a hung order that reads like a Route53 failure"
        }
        else {
            Add-Check -Step '3b.7' -Name 'DNS-01 uses public resolvers' -Status 'Pass' -Detail (($controllerArgs | Where-Object { $_ -like '--dns01*' }) -join ' ')
        }
    }

    # --- 3b.8 ---------------------------------------------------------
    if ($null -eq $ingressVip) {
        Add-Check -Step '3b.8' -Name 'kube-vip address pool' -Status 'Fail' -Detail 'INGRESS_VIP is not in the ConfigMap, so the pool cannot be checked against anything'
    }
    else {
        $range = [string](Get-Path $kubeVipPool 'data.range-global')
        $expectedRange = "$ingressVip-$ingressVip"
        if ($range -eq $expectedRange) {
            Add-Check -Step '3b.8' -Name 'kube-vip address pool' -Status 'Pass' -Detail $range
        }
        else {
            Add-Check -Step '3b.8' -Name 'kube-vip address pool' -Status 'Fail' `
                -Detail "ConfigMap kubevip in kube-system has range-global '$range', expected '$expectedRange'. A wider range lets the cloud provider hand a second Service an address nothing has reserved on the LAN"
        }
    }

    # --- 3b.9 ---------------------------------------------------------
    # Exit: the Service shows EXTERNAL-IP = ${INGRESS_VIP}. `status`, not
    # `spec`: the address is assigned by the cloud provider and published in
    # status, and a Service whose status is empty is exactly the failure the
    # manifest's long note describes - the VIP answers ARP, kube-proxy programs
    # nothing, and every connection is refused with every object Ready.
    if ($null -eq $helmChartConfig -or -not (Get-Path $helmChartConfig 'metadata.name')) {
        Add-Check -Step '3b.9' -Name 'Traefik HelmChartConfig' -Status 'Fail' -Detail 'no HelmChartConfig named traefik in kube-system - k3s is serving its own default values'
    }
    else {
        Add-Check -Step '3b.9' -Name 'Traefik HelmChartConfig' -Status 'Pass'
    }

    $publishedIps = @(Get-Path $traefikService 'status.loadBalancer.ingress' | Where-Object { $_ } | ForEach-Object { [string](Get-Field $_ 'ip') } | Where-Object { $_ })
    if ($null -eq $ingressVip) {
        Add-Check -Step '3b.9' -Name 'Traefik Service EXTERNAL-IP' -Status 'Fail' -Detail 'INGRESS_VIP is not in the ConfigMap'
    }
    elseif ($publishedIps -contains $ingressVip) {
        Add-Check -Step '3b.9' -Name 'Traefik Service EXTERNAL-IP' -Status 'Pass' -Detail $ingressVip
    }
    elseif ($publishedIps.Count -eq 0) {
        Add-Check -Step '3b.9' -Name 'Traefik Service EXTERNAL-IP' -Status 'Fail' `
            -Detail '<pending> - the Service has no published address. kube-proxy builds its rules from this status, so nothing catches 80/443 even while the VIP answers ARP'
    }
    else {
        Add-Check -Step '3b.9' -Name 'Traefik Service EXTERNAL-IP' -Status 'Fail' -Detail "published $($publishedIps -join ', '), expected $ingressVip"
    }

    # The annotation, not spec.loadBalancerIP. Both end at the same place; only
    # this one is present in every render, which is what closes the window
    # where kube-vip sees an address-less Service and dies before it patches
    # status. Diagnosed on 2026-08-16 and recorded in the manifest.
    $annotation = [string](Get-MapValue (Get-Path $traefikService 'metadata.annotations') 'kube-vip.io/loadbalancerIPs')
    if ($null -ne $ingressVip -and $annotation -eq $ingressVip) {
        Add-Check -Step '3b.9' -Name 'Traefik Service carries the VIP annotation' -Status 'Pass' -Detail 'kube-vip.io/loadbalancerIPs'
    }
    elseif ($publishedIps -contains $ingressVip) {
        # Assigned but not annotated in the render: works today, and re-opens
        # the failure window on the next helm upgrade.
        Add-Check -Step '3b.9' -Name 'Traefik Service carries the VIP annotation' -Status 'Warn' `
            -Detail "annotation is '$annotation'. The address is assigned, but if it arrived via the deprecated spec.loadBalancerIP the next chart render is address-less and kube-vip can fail before publishing status"
    }
    else {
        Add-Check -Step '3b.9' -Name 'Traefik Service carries the VIP annotation' -Status 'Fail' -Detail "annotation is '$annotation', expected $ingressVip"
    }

    # --- 3b.10 --------------------------------------------------------
    foreach ($name in @('letsencrypt-staging', 'letsencrypt-prod')) {
        $issuer = $clusterIssuers | Where-Object { (Get-Path $_ 'metadata.name') -eq $name } | Select-Object -First 1
        [void](Add-ObjectReadyCheck -Step '3b.10' -Name "ClusterIssuer $name" -Object $issuer)
    }

    $certificate = $certificates | Where-Object {
        (Get-Path $_ 'metadata.name') -eq 'aerie-wildcard' -and (Get-Path $_ 'metadata.namespace') -eq 'kube-system'
    } | Select-Object -First 1
    if (Add-ObjectReadyCheck -Step '3b.10' -Name 'Certificate aerie-wildcard' -Object $certificate) {
        $issuerName = [string](Get-Path $certificate 'spec.issuerRef.name')
        if ($issuerName -eq 'letsencrypt-prod') {
            Add-Check -Step '3b.10' -Name 'Wildcard is issued by production' -Status 'Pass' -Detail $issuerName
        }
        else {
            Add-Check -Step '3b.10' -Name 'Wildcard is issued by production' -Status 'Fail' `
                -Detail "issuerRef is '$issuerName'. This is the one deliberate stop in 3b: diagnose against staging, then change that one word to letsencrypt-prod and commit"
        }

        $dnsNames = @(Get-Path $certificate 'spec.dnsNames' | Where-Object { $_ })
        if ($null -eq $domain) {
            Add-Check -Step '3b.10' -Name 'Wildcard covers the apex' -Status 'Fail' -Detail 'DOMAIN is not in the ConfigMap'
        }
        elseif ($dnsNames -contains "*.$domain" -and $dnsNames -contains $domain) {
            Add-Check -Step '3b.10' -Name 'Wildcard covers the apex' -Status 'Pass' -Detail ($dnsNames -join ', ')
        }
        else {
            Add-Check -Step '3b.10' -Name 'Wildcard covers the apex' -Status 'Fail' `
                -Detail "dnsNames are $($dnsNames -join ', '); a wildcard does not match its own apex, so both *.$domain and $domain have to be listed"
        }
    }

    # Traefik keys the store named `default` cluster-wide rather than by
    # namespace, and finding two it deletes the default store outright - every
    # hostname silently back on the self-signed certificate, with a log line as
    # the only symptom.
    $defaultStores = @($tlsStores | Where-Object { (Get-Path $_ 'metadata.name') -eq 'default' })
    if ($defaultStores.Count -eq 1) {
        $secretName = [string](Get-Path $defaultStores[0] 'spec.defaultCertificate.secretName')
        $storeNamespace = [string](Get-Path $defaultStores[0] 'metadata.namespace')
        if ($secretName -eq 'aerie-wildcard-tls' -and $storeNamespace -eq 'kube-system') {
            Add-Check -Step '3b.10' -Name 'TLSStore default' -Status 'Pass' -Detail "kube-system, $secretName"
        }
        else {
            Add-Check -Step '3b.10' -Name 'TLSStore default' -Status 'Fail' `
                -Detail "$storeNamespace/$secretName. The store resolves its Secret in its own namespace with no cross-namespace path at all, so both it and the certificate have to sit beside the Traefik k3s ships"
        }
    }
    elseif ($defaultStores.Count -eq 0) {
        Add-Check -Step '3b.10' -Name 'TLSStore default' -Status 'Fail' -Detail 'absent - the wildcard is issued, stored, and never presented; Traefik answers every route with its built-in self-signed certificate'
    }
    else {
        Add-Check -Step '3b.10' -Name 'TLSStore default' -Status 'Fail' `
            -Detail "$($defaultStores.Count) stores named 'default' ($(($defaultStores | ForEach-Object { Get-Path $_ 'metadata.namespace' }) -join ', ')). Traefik deletes the default store outright when it finds more than one, which puts every hostname back on the self-signed certificate"
    }

    # --- 3b.11 --------------------------------------------------------
    # The setting is a read-back, not a restatement of the manifest: Longhorn
    # ships no values schema and its manager logs and *skips* a value that
    # fails to parse or falls out of range, so a wrong replica count is not a
    # failed install - it is Longhorn quietly running on 3.
    #
    # The value arrives in one of two shapes - a bare count, or one count per
    # data engine - so it is compared per engine. Every engine present has to
    # match, because both are written from the single ${LONGHORN_REPLICA_COUNT}
    # token: a divergence between v1 and v2 means something other than this
    # repository set one of them. An operator who wants them to differ has a
    # one-line edit here and a reason to write down.
    $settingValues = @(Get-LonghornSettingValues -Value $longhornReplicas)
    if ($null -eq $replicaCount) {
        Add-Check -Step '3b.11' -Name 'default-replica-count setting' -Status 'Fail' -Detail 'LONGHORN_REPLICA_COUNT is not in the ConfigMap'
    }
    elseif ($settingValues.Count -eq 0) {
        Add-Check -Step '3b.11' -Name 'default-replica-count setting' -Status 'Fail' `
            -Detail $(if ([string]::IsNullOrWhiteSpace($longhornReplicas)) {
                    'the Setting default-replica-count has no value, or does not exist - Longhorn creates it on first start, so an empty one means the manager never came up'
                }
                else {
                    "Longhorn reports '$longhornReplicas', which is neither a count nor a per-engine JSON object. Longhorn changes this shape between versions; the reader in Get-LonghornSettingValues needs teaching about the new one"
                })
    }
    else {
        $describe = ($settingValues | ForEach-Object {
                if ($_.Engine) { "$($_.Engine)=$($_.Value)" } else { $_.Value }
            }) -join ', '
        $wrong = @($settingValues | Where-Object { $_.Value -ne $replicaCount })
        if ($wrong.Count -eq 0) {
            Add-Check -Step '3b.11' -Name 'default-replica-count setting' -Status 'Pass' -Detail $describe
        }
        else {
            Add-Check -Step '3b.11' -Name 'default-replica-count setting' -Status 'Fail' `
                -Detail "Longhorn reports $describe, the ConfigMap says '$replicaCount'. Longhorn skips a setting it cannot parse and logs about it, so the install succeeds either way"
        }
    }

    $expectedClasses = @(
        @{ Name = 'longhorn'; Replicas = $replicaCount; Default = $false; Why = 'the class the chart installs, which reads persistence.defaultClassReplicaCount and not the setting above' }
        @{ Name = 'longhorn-r3'; Replicas = '3'; Default = $false; Why = 'tiny critical volumes' }
        @{ Name = 'longhorn-r2'; Replicas = '2'; Default = $false; Why = 'bulk volumes where loss is tolerable' }
        @{ Name = 'local-path'; Replicas = $null; Default = $true; Why = "k3s's own, and the storage split deliberately puts Postgres on it" }
    )
    foreach ($expected in $expectedClasses) {
        $class = $storageClasses | Where-Object { (Get-Path $_ 'metadata.name') -eq $expected.Name } | Select-Object -First 1
        if ($null -eq $class) {
            Add-Check -Step '3b.11' -Name "StorageClass $($expected.Name)" -Status 'Fail' -Detail "absent - $($expected.Why)"
            continue
        }

        $problems = New-Object Collections.Generic.List[string]
        if ($null -ne $expected.Replicas) {
            $actual = [string](Get-Path $class 'parameters.numberOfReplicas')
            if ($actual -ne $expected.Replicas) { $problems.Add("numberOfReplicas is '$actual', expected '$($expected.Replicas)'") }
        }
        # Two default classes make any PVC that omits a class undefined, which
        # is a coin flip between replicated storage and a node-local directory.
        $isDefault = ((Get-MapValue (Get-Path $class 'metadata.annotations') 'storageclass.kubernetes.io/is-default-class') -eq 'true')
        if ($isDefault -ne $expected.Default) {
            $problems.Add($(if ($isDefault) { 'is the default StorageClass and should not be' } else { 'is not the default StorageClass and should be' }))
        }

        if ($problems.Count -gt 0) {
            Add-Check -Step '3b.11' -Name "StorageClass $($expected.Name)" -Status 'Fail' -Detail ($problems -join '; ')
        }
        else {
            Add-Check -Step '3b.11' -Name "StorageClass $($expected.Name)" -Status 'Pass' -Detail $(if ($null -ne $expected.Replicas) { "$($expected.Replicas) replica(s)" } else { 'default' })
        }
    }

    # Exit: each disk schedulable at the data disk's capacity - which is what
    # actually proves 3b.2 worked, more than `df` does, because it is Longhorn
    # rather than the shell reporting what it found.
    $capacityFloor = [int64]$DataDiskSizeGB * 1GB * (100 - $SizeTolerancePercent - 5) / 100
    if ($longhornNodes.Count -eq 0) {
        Add-Check -Step '3b.11' -Name 'Longhorn node disks' -Status 'Fail' -Detail 'no nodes.longhorn.io objects - the manager has not registered any node'
    }
    elseif ($nodes.Count -gt 0 -and $longhornNodes.Count -ne $nodes.Count) {
        Add-Check -Step '3b.11' -Name 'Longhorn node disks' -Status 'Fail' -Detail "$($longhornNodes.Count) Longhorn node(s) against $($nodes.Count) Kubernetes node(s)"
    }
    else {
        $diskProblems = New-Object Collections.Generic.List[string]
        $capacities = New-Object Collections.Generic.List[string]
        foreach ($longhornNode in $longhornNodes) {
            $nodeName = [string](Get-Path $longhornNode 'metadata.name')
            $diskStatus = Get-Path $longhornNode 'status.diskStatus'
            if ($null -eq $diskStatus) { $diskProblems.Add("$nodeName reports no disks"); continue }

            $schedulable = $false
            foreach ($property in $diskStatus.PSObject.Properties) {
                $disk = $property.Value
                $maximum = [int64]0
                [void][int64]::TryParse([string](Get-Field $disk 'storageMaximum'), [ref]$maximum)
                $conditions = @(Get-Field $disk 'conditions' | Where-Object { $_ })
                $isSchedulable = @($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Schedulable' -and (Get-Field $_ 'status') -eq 'True' }).Count -gt 0
                if ($isSchedulable -and $maximum -ge $capacityFloor) {
                    $schedulable = $true
                    $capacities.Add("$nodeName $(Format-Size $maximum)")
                    break
                }
                if ($isSchedulable) {
                    $diskProblems.Add("$nodeName has a schedulable disk of only $(Format-Size $maximum) - that is the OS disk, not the $DataDiskSizeGB GB one Provision 5 mounts at $MountPoint")
                }
            }
            if (-not $schedulable -and -not ($diskProblems | Where-Object { $_ -like "$nodeName*" })) {
                $diskProblems.Add("$nodeName has no schedulable disk")
            }
        }
        if ($diskProblems.Count -gt 0) {
            Add-Check -Step '3b.11' -Name 'Longhorn node disks' -Status 'Fail' -Detail ($diskProblems -join '; ')
        }
        else {
            Add-Check -Step '3b.11' -Name 'Longhorn node disks' -Status 'Pass' -Detail ($capacities -join ', ')
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Nodes'
    # ---------------------------------------------------------------- #

    # Every node the cluster reports, not the one this run was pointed at.
    # 3b.2 is per-node work - a mounted disk is not shared through etcd - so a
    # gate that asks one node has checked one third of the property.
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
        Add-Check -Step '3b.2' -Name "$MountPoint on every node" -Status 'Fail' -Detail 'no node reported an InternalIP, so no node could be reached'
    }

    $vipHolders = New-Object Collections.Generic.List[string]
    foreach ($node in $nodeAddresses) {
        $label = "$($node.Name) ($($node.Address))"

        if (-not (Test-TcpPort -IPAddress $node.Address -Port 22)) {
            Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Fail' -Detail "$($node.Address) is not answering on port 22 from here"
            continue
        }

        $nodeSsh = @{ IPAddress = $node.Address; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }
        $nodeProbe = Invoke-NodeSsh @nodeSsh -ConnectTimeoutSec 20 -Command (@(
                'echo ''--- findmnt'''
                "findmnt -n -b -o SOURCE,TARGET,FSTYPE,SIZE $MountPoint 2>/dev/null || echo none"
                'echo ''--- addr'''
                $(if ($nodeInterface) { "ip -o -4 addr show dev $nodeInterface 2>/dev/null || echo none" } else { 'echo none' })
                'echo ''--- end'''
            ) -join '; ')

        if ($nodeProbe.ExitCode -ne 0) {
            Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Fail' -Detail "SSH to $label failed: $(($nodeProbe.StdErr -replace '\s+', ' ').Trim())"
            continue
        }

        $findmnt = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'findmnt').Trim()
        if (-not $findmnt -or $findmnt -eq 'none') {
            Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Fail' `
                -Detail "nothing is mounted at $MountPoint. Longhorn's data path defaults here on the *root* filesystem, so this node is filling its OS disk with replica data - dispatch Provision 5 against it"
        }
        else {
            # SOURCE TARGET FSTYPE SIZE, in that order, with -b so SIZE is bytes.
            $fields = @($findmnt -split '\s+')
            $mountBytes = [int64]0
            if ($fields.Count -ge 4) { [void][int64]::TryParse($fields[3], [ref]$mountBytes) }
            if ($fields.Count -lt 3 -or $fields[2] -ne 'ext4') {
                Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Fail' -Detail "findmnt says '$findmnt', which is not the ext4 mount Provision 5 creates"
            }
            elseif ($mountBytes -lt $capacityFloor) {
                Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Fail' `
                    -Detail "$(Format-Size $mountBytes), well under the $DataDiskSizeGB GB data disk - this is a different disk, or the root filesystem"
            }
            else {
                Add-Check -Step '3b.2' -Name "$MountPoint on $($node.Name)" -Status 'Pass' -Detail "$($fields[0]), ext4, $(Format-Size $mountBytes)"
            }
        }

        $addresses = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'addr')
        if ($ingressVip -and $addresses -match ("\s" + [regex]::Escape($ingressVip) + "/")) {
            $vipHolders.Add($node.Name)
        }
    }

    # Exit: `ip addr show ${NODE_INTERFACE}` on the elected leader shows the
    # VIP. Asked of every node rather than of a Lease, which is the stronger
    # form of the same question: exactly one node may answer ARP for it, and
    # two that do is an address conflict that presents as intermittent packet
    # loss rather than as anything a controller reports.
    if (-not $ingressVip -or -not $nodeInterface) {
        Add-Check -Step '3b.8' -Name 'Exactly one node holds the VIP' -Status 'Fail' -Detail 'INGRESS_VIP or NODE_INTERFACE is not in the ConfigMap'
    }
    elseif ($vipHolders.Count -eq 1) {
        Add-Check -Step '3b.8' -Name 'Exactly one node holds the VIP' -Status 'Pass' -Detail "$($vipHolders[0]) on $nodeInterface"
    }
    elseif ($vipHolders.Count -eq 0) {
        Add-Check -Step '3b.8' -Name 'Exactly one node holds the VIP' -Status 'Fail' `
            -Detail "no node carries $ingressVip on $nodeInterface. kube-vip installs cleanly against a wrong vip_interface and simply never answers ARP"
    }
    else {
        Add-Check -Step '3b.8' -Name 'Exactly one node holds the VIP' -Status 'Fail' `
            -Detail "$($vipHolders.Count) nodes carry it ($($vipHolders -join ', ')) - an IP conflict, not a hot spare"
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'LAN'
    # ---------------------------------------------------------------- #

    if (-not $ingressVip) {
        Add-Check -Step '3b.8' -Name 'VIP answers from the LAN' -Status 'Fail' -Detail 'INGRESS_VIP is not in the ConfigMap'
    }
    else {
        # ICMP is the advisory half: a reply proves the VIP is live, and its
        # absence proves only that something did not forward an echo request.
        # The handshake below is what the gate turns on.
        $pinged = $false
        try { $pinged = Test-Connection -ComputerName $ingressVip -Count 2 -Quiet -ErrorAction Stop }
        catch { $pinged = $false }
        if ($pinged) {
            Add-Check -Step '3b.8' -Name 'VIP answers from the LAN' -Status 'Pass' -Detail "ping $ingressVip"
        }
        else {
            Add-Check -Step '3b.8' -Name 'VIP answers from the LAN' -Status 'Warn' `
                -Detail "no ICMP reply from $ingressVip. Advisory only - the TLS check below is the one that matters, and ICMP is filtered on plenty of healthy networks"
        }

        if (-not $domain) {
            Add-Check -Step '3b.9' -Name 'Traefik answers on the VIP' -Status 'Fail' -Detail 'DOMAIN is not in the ConfigMap, so there is no name to send as SNI'
        }
        else {
            $serverName = "home.$domain"
            $tls = Invoke-TlsProbe -IPAddress $ingressVip -ServerName $serverName

            if (-not $tls.Connected) {
                Add-Check -Step '3b.9' -Name 'Traefik answers on the VIP' -Status 'Fail' `
                    -Detail "${ingressVip}:443 - $($tls.Error). A refused connection with every object Ready is the kube-proxy-has-no-rules failure 3b.9's manifest documents: check the Service's published address above"
                Add-Check -Step '3b.10' -Name 'Served certificate is the production wildcard' -Status 'Fail' -Detail 'no TLS handshake, so nothing was served to inspect'
            }
            else {
                # Any HTTP status is a pass here, and the reasoning is worth
                # stating: 404 is the correct answer *today*, with no routes
                # defined, and Phase 5 makes it a 200 or a redirect without
                # anything having regressed. What is being proven is that the
                # VIP reaches Traefik and Traefik speaks HTTP over TLS on it.
                if ($tls.StatusLine -match '^HTTP/1\.[01] (\d{3})') {
                    $code = $Matches[1]
                    $note = if ($code -eq '404') { '404, the correct answer with no routes defined' } else { "$code - Traefik is routing, which from Phase 5 on is expected" }
                    Add-Check -Step '3b.9' -Name 'Traefik answers on the VIP' -Status 'Pass' -Detail $note
                }
                else {
                    Add-Check -Step '3b.9' -Name 'Traefik answers on the VIP' -Status 'Fail' `
                        -Detail "TLS completed but the answer was not HTTP: '$($tls.StatusLine)'"
                }

                if ($null -eq $tls.Certificate) {
                    Add-Check -Step '3b.10' -Name 'Served certificate is the production wildcard' -Status 'Fail' -Detail 'the handshake completed without a peer certificate'
                }
                else {
                    $issuer = $tls.Certificate.Issuer
                    $names = @(Get-CertificateDnsNames -Certificate $tls.Certificate)
                    $expires = $tls.Certificate.NotAfter
                    $problems = New-Object Collections.Generic.List[string]

                    # Let's Encrypt's staging hierarchy carries (STAGING) in
                    # the issuer's own CN and O, which is the whole reason the
                    # exit criterion reads the issuer rather than just checking
                    # that a name matched.
                    if ($issuer -match 'STAGING') {
                        $problems.Add("issued by the Let's Encrypt *staging* hierarchy ($issuer) - the 3b.10 flip to letsencrypt-prod has not taken effect")
                    }
                    elseif ($issuer -notmatch "Let's Encrypt") {
                        $problems.Add("issued by '$issuer' - if that is TRAEFIK DEFAULT CERT, the TLSStore is not serving the wildcard")
                    }
                    if ($names -notcontains "*.$domain") { $problems.Add("subjectAltName does not include *.$domain (has: $($names -join ', '))") }
                    if ($expires -lt (Get-Date)) { $problems.Add("expired on $($expires.ToString('yyyy-MM-dd'))") }

                    if ($problems.Count -gt 0) {
                        Add-Check -Step '3b.10' -Name 'Served certificate is the production wildcard' -Status 'Fail' -Detail ($problems -join '; ')
                    }
                    else {
                        Add-Check -Step '3b.10' -Name 'Served certificate is the production wildcard' -Status 'Pass' `
                            -Detail "$($names -join ', '), expires $($expires.ToString('yyyy-MM-dd'))"
                    }
                }
            }
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Portability'
    # ---------------------------------------------------------------- #

    # The cluster plan's Verification section: no phase is done if a second
    # operator could not run it on their own hardware. 3b.13 asks for the grep
    # explicitly - the base domain, any LAN address, the VIP - and this is the
    # only place it can honestly be made, because it needs the repository and
    # this installation's actual values at the same time. ci.yml carries the
    # half that needs neither: no IPv4 literal anywhere under deploy/.
    #
    # NODE_INTERFACE and LONGHORN_REPLICA_COUNT are excluded on purpose. They
    # are per-installation values like the rest, but 'eth0' and '2' appear in
    # prose in almost every file here, so grepping for them would produce a
    # permanent wall of false positives - which is how a check gets turned off.
    $portabilityKeys = @($configValues.Keys | Where-Object { $_ -notin @('NODE_INTERFACE', 'LONGHORN_REPLICA_COUNT') } | Sort-Object)
    if ($portabilityKeys.Count -eq 0) {
        Add-Check -Step '3b.13' -Name 'No installation values in deploy/' -Status 'Fail' -Detail 'the ConfigMap yielded no values to grep for'
    }
    else {
        $deployFiles = @(Get-ChildItem -Path $DeployPath -Recurse -File)
        $leaks = New-Object Collections.Generic.List[string]
        foreach ($key in $portabilityKeys) {
            $value = $configValues[$key]
            if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -lt 4) { continue }
            $hits = @($deployFiles | Select-String -SimpleMatch -Pattern $value -ErrorAction SilentlyContinue)
            foreach ($hit in $hits) {
                $relative = $hit.Path.Substring((Resolve-Path $DeployPath).Path.Length).TrimStart('\', '/')
                $leaks.Add("$key at deploy/$relative`:$($hit.LineNumber)")
            }
        }
        if ($leaks.Count -gt 0) {
            Add-Check -Step '3b.13' -Name 'No installation values in deploy/' -Status 'Fail' `
                -Detail "$($leaks -join '; '). Every one of these belongs in the tree as a `${...} substitution and nowhere else - see docs/ethos.md"
        }
        else {
            Add-Check -Step '3b.13' -Name 'No installation values in deploy/' -Status 'Pass' -Detail "$($deployFiles.Count) file(s) checked against $($portabilityKeys.Count) value(s)"
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
        "## Phase 3 platform gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { '✅' } 'Warn' { '⚠️' } default { '❌' } }
        # Pipes would break the table, and a controller message containing one
        # is not hypothetical.
        $detail = ($check.Detail -replace '\|', '\|')
        $lines += "| $($check.Step) | $($check.Check) | $mark $($check.Result) | $detail |"
    }
    $lines += @(
        ''
        '_The cluster plan, Phase 3b.13. Read-only: this run changed nothing._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 3 platform gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names the step it belongs to; the cluster plan Phase 3b has the reasoning for each.'
    exit 1
}

Write-Host "Phase 3 platform gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'Every *Exit* criterion in the cluster plan Phase 3b.1-3b.12 has been asserted against this'
Write-Host 'cluster, from a LAN client where that is what the criterion means. Re-run this after a node'
Write-Host 'rebuild or a restore; it is read-only and safe at any time.'
exit 0
