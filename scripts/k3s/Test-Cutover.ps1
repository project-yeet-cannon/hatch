<#
.SYNOPSIS
    Asserts 7c.11's checklist in one run - the Phase 7 gate, and the last
    box in docs/plans/swarm/phase-7-cutover.md. "The cutover is done" as a
    command rather than as a memory.

.DESCRIPTION
    The same shape scripts/k3s/Test-ClusterPlatform.ps1 (3b.13),
    Test-DataTier.ps1 (4b.11), Test-AppTier.ps1 (5b.14) and
    Test-Observability.ps1 (6b.15) established: read-only, not numbered into
    the Provision sequence, does not stop at the first failure, and a check it
    cannot evaluate is a failure rather than a skip.

    One thing distinguishes it from every gate before it, and it is the reason
    it exists as its own script rather than as more assertions bolted onto the
    others: **it resolves names normally.** Every earlier gate reaches the
    cluster by dialling ${INGRESS_VIP} directly and sending the hostname as
    SNI - curl's --resolve by another name - because until 7b.4 pointed
    pfSense Unbound at the VIP there was no record to follow. This one must
    not: the property under test is that the house's own resolver sends the
    house to the cluster, and a check that supplies the answer cannot see it.

    So the Resolution and Fetch stages below are lifted from
    Test-NameResolution.ps1 (7b.6) rather than written again - its DNS query,
    its socket-peer read and its hosts-file precondition are exactly the three
    pieces 7c.11's second bullet asks for. They are copied rather than shared
    through a library for the same reason every gate here carries its own
    Get-Field/Get-Path/Add-Check: each of these scripts is a single file that
    runs from wherever it has to run, and Test-NameResolution.ps1's own header
    makes a point of depending on nothing but PowerShell.

    Where this script differs from that one: **the expectation comes from the
    cluster, the answer comes from the resolver.** Test-NameResolution.ps1
    takes -Domain and -IngressVip as parameters because it deliberately holds
    no cluster credentials; this gate already has an SSH key in its hand, so
    DOMAIN and INGRESS_VIP are read out of the live aerie-cluster-config
    ConfigMap like every other gate reads its expectations. Only the *answer*
    is allowed to come from DNS.

    Stages:
      1. Preflight  - the SSH key resolves, the node answers 22, the
                      repository root is the tree this script expects.
      2. Probe      - one SSH round trip: every cluster object this script
                      reasons about, plus Prometheus's active target list
                      through the API server's generic service proxy.
      3. Nodes      - one SSH connection per node: etcd's :2381, its leader,
                      and how many peers each member can see. Membership is a
                      property of each machine, not of the control plane, and
                      Test-ClusterPlatform.ps1's Nodes stage is the precedent.
      4. Checks     - 7c.11's cluster-side checklist, against those snapshots.
      5. Resolution - the seven hostnames, resolved by this client and by the
                      resolver it is configured to use. No --resolve, no
                      hosts file, and the address asserted as an exact set.
      6. Fetch      - one TLS connection per hostname, opened *by name*, and
                      the address the socket actually reached read back off
                      it. That read is curl's %{remote_ip} and it is the only
                      value here that proves the cluster answered.
      7. Tree       - the compose path is gone, the local-dev path survives,
                      and the old Docker host's address appears nowhere. A
                      half-finished deletion is this phase's characteristic
                      failure and it is a file check, not a cluster one.
      8. Report     - one table, one exit code.

    **This run is the LAN half.** A self-hosted runner is a client on the
    house LAN, resolving through the same pfSense Unbound 7b.4 repointed,
    which is the vantage point 7c.11 names. The tailnet half stays with
    Test-NameResolution.ps1, run by hand from a tailnet client - this script
    reports which vantage point it ran from rather than letting a green table
    imply both.

    ASCII only, in code and in comments. Windows PowerShell 5.1 decodes a
    BOM-less script as the ANSI code page rather than as UTF-8, and an em
    dash's third byte is a curly quote there, which 5.1 treats as a real
    string delimiter. Same rule Test-NameResolution.ps1 follows and for the
    same reason: this file is run by hand from unpredictable places.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read over SSH in the
    Probe stage is cluster state, the same as every other gate here.

.PARAMETER LegacyHostAddress
    The LAN address of the Docker host this phase retired - the one value the
    portability check greps the tree for, since this is the phase that retires
    it. Per-installation, so it is a parameter rather than a constant;
    defaults to $env:LEGACY_HOST_ADDRESS, which is what the workflow wrapper
    sets it from. Note that after 7c.4 the *rebuilt* host usually keeps this
    address, so it legitimately appears in WINDOWS_EXPORTER_TARGETS and
    SHARE_HOST in the ConfigMap - what must not exist is a literal in the
    tree.

.PARAMETER RepositoryRoot
    The checkout the Tree stage reads. Defaults to this script's own
    repository, which is the only thing that makes sense on a runner.

.PARAMETER Resolver
    One or more resolver addresses to query directly, in addition to this
    client's own stack. Defaults to whatever this client is configured to
    use - on the LAN that is pfSense, which is the resolver 7c.11 means.

.EXAMPLE
    .\Test-Cutover.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519 -LegacyHostAddress 10.0.0.9
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [string]$LegacyHostAddress = $env:LEGACY_HOST_ADDRESS,

    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),

    [string]$ScheduledBackupManifestPath = (Join-Path $PSScriptRoot '..\..\deploy\cluster\data\schema\scheduledbackup.yaml'),

    [string[]]$Resolver,

    [ValidateRange(2, 60)]
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

$script:Checks = New-Object Collections.Generic.List[psobject]
$script:StageNumber = 0
$script:TimeoutMs = $TimeoutSeconds * 1000

# $IsWindows does not exist on 5.1, and Set-StrictMode turns reading it there
# into a terminating error rather than $null - so it is derived instead of
# read. RuntimeInformation is present on both editions.
$script:OnWindows = $true
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $script:OnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
}

function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

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

function Get-Field {
    <#
    .SYNOPSIS
        Reads a property off a ConvertFrom-Json object, returning $null when
        it is absent instead of throwing under Set-StrictMode.
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
        link is missing. See Test-ClusterPlatform.ps1's copy for why that
        matters for a gate: an absent path and an absent value both mean
        "not proven".
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
        The .items of a kubectl list, as an array that is empty rather than
        $null when there are none.
    #>
    param([Parameter(Mandatory)][AllowNull()]$List)
    return @(Get-Field $List 'items' | Where-Object { $_ })
}

function Get-MapValue {
    <#
    .SYNOPSIS
        One key out of a ConfigMap's data, without Get-Path.

    .DESCRIPTION
        Get-Path splits on every '.' with no escaping, so it cannot read a
        key that contains one. None of the keys here does today, but the
        ConfigMap is a map of operator-supplied names and a gate that starts
        silently reading $null the day one gains a dot is worse than one
        extra function. Test-ClusterPlatform.ps1 carries the same helper for
        the same reason.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$ConfigMap,
        [Parameter(Mandatory)][string]$Key
    )
    $data = Get-Field $ConfigMap 'data'
    if ($null -eq $data) { return $null }
    return (Get-Field $data $Key)
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
        ConvertFrom-Json as one object rather than enumerating it, so
        @($json | ConvertFrom-Json) is a one-element array *wrapping* the
        data there and the real array under 7. Every downstream .Count and
        -join then describes the wrapper. WINDOWS_EXPORTER_TARGETS is exactly
        that shape, and ../../.github/workflows/verify-cutover.yml runs under
        `shell: powershell` - which is 5.1.
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
    return $items.ToArray()
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
        02:00:02'), and re-parsing that assumes local time. On a machine four
        hours behind UTC that turns a backup taken four hours ago into one
        taken now, and the age check silently stops being able to see a stale
        backup within a whole UTC offset of the threshold. Found while
        building 7c.11's gate; scripts/k3s/Test-DataTier.ps1's 4b.10 check
        had the same trap and is fixed alongside.

        The same cast also breaks *sorting*: 'MM/dd/yyyy' orders by month
        before year, so "the newest Backup" picked by string comparison is
        only correct within one December.

        Kind is handled rather than assumed, because the two editions differ:
        PowerShell 7 returns Kind=Utc for a 'Z' input, Windows PowerShell 5.1
        has historically returned the same instant as Kind=Local, and the
        workflow wrapper runs 5.1. Unspecified is read as UTC - every
        timestamp this script reads comes from the Kubernetes API server,
        which emits nothing else.
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
        line-matching, not a YAML parser. Test-DataTier.ps1's copy has the
        argument for why that is proportionate here: one cron expression,
        out of this repository's own committed file.
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

function Invoke-Git {
    <#
    .SYNOPSIS
        One git command against the checkout, returning its exit code and
        output instead of throwing - `git grep` uses exit code 1 to mean
        "found nothing", which is this gate's *passing* answer.
    #>
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        # Function-scoped, the same reason AerieSsh.ps1's Invoke-NodeSsh
        # drops it: under Stop a native command writing to stderr raises a
        # terminating NativeCommandError, and git writes advice there.
        $ErrorActionPreference = 'Continue'
        # safe.directory, per invocation and never written to any config:
        # on a self-hosted runner the workspace is owned by the runner
        # service account rather than by whoever the step runs as, and git
        # refuses to read a repository it considers dubiously owned.
        # actions/checkout does add the exception - into a *temporary* HOME
        # it discards when its own step ends, so it is gone by the time any
        # later step runs git. The path is spelled with forward slashes
        # because that is the form git normalises to and compares against,
        # which its own error message quotes back.
        $safeDirectory = $RepositoryRoot.Replace('\', '/')
        & git -c "safe.directory=$safeDirectory" -C $RepositoryRoot @Arguments 1> $stdoutFile 2> $stderrFile
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exitCode
            StdOut   = (Get-Content -Raw -Path $stdoutFile -ErrorAction SilentlyContinue)
            StdErr   = (Get-Content -Raw -Path $stderrFile -ErrorAction SilentlyContinue)
        }
    }
    finally {
        Remove-Item $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

function Get-ExceptionMessage {
    <#
    .SYNOPSIS
        The innermost exception's message, without PowerShell's
        'Exception calling "X" with "1" argument(s)' wrapper around it.
    #>
    param([Parameter(Mandatory)]$ErrorRecord)
    $exception = $ErrorRecord.Exception
    while ($exception.InnerException) { $exception = $exception.InnerException }
    return $exception.Message
}

function Get-HostsFilePath {
    if ($script:OnWindows) {
        $root = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
        return (Join-Path $root 'System32\drivers\etc\hosts')
    }
    return '/etc/hosts'
}

function Get-ConfiguredResolver {
    <#
    .SYNOPSIS
        The resolver addresses this client is configured to use, as IPv4
        strings, discovered without assuming a platform. Lifted from
        Test-NameResolution.ps1, whose copy carries the full reasoning.
    #>
    $found = New-Object Collections.Generic.List[string]

    if ($script:OnWindows) {
        try {
            $up = @(Get-NetAdapter -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { $_.InterfaceIndex })
            foreach ($entry in @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop)) {
                if ($up.Count -gt 0 -and $up -notcontains $entry.InterfaceIndex) { continue }
                foreach ($address in @($entry.ServerAddresses)) {
                    if ($address -and $found -notcontains $address) { $found.Add($address) }
                }
            }
        }
        catch {
            Write-Verbose "DNS client discovery failed: $($_.Exception.Message)"
        }
    }
    elseif (Test-Path '/etc/resolv.conf') {
        foreach ($line in (Get-Content '/etc/resolv.conf')) {
            if ($line -match '^\s*nameserver\s+([0-9]{1,3}(\.[0-9]{1,3}){3})\s*$') {
                $address = $Matches[1]
                if ($found -notcontains $address) { $found.Add($address) }
            }
        }
    }

    return @($found | Where-Object { $_ -ne '127.0.0.53' -and $_ -ne '127.0.0.1' })
}

function Get-ClientVantagePoint {
    <#
    .SYNOPSIS
        Where this run is seeing the world from: on the VIP's own subnet, on
        the tailnet, or neither - reported rather than asserted. Lifted from
        Test-NameResolution.ps1.
    #>
    param([Parameter(Mandatory)][string]$IngressVip)

    $result = [pscustomobject]@{ OnVipSubnet = $false; VipSubnetDetail = ''; TailnetAddress = '' }
    $vipBytes = ([Net.IPAddress]::Parse($IngressVip)).GetAddressBytes()

    foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up') { continue }
        foreach ($unicast in $nic.GetIPProperties().UnicastAddresses) {
            if ($unicast.Address.AddressFamily -ne 'InterNetwork') { continue }
            $local = $unicast.Address.GetAddressBytes()

            # 100.64.0.0/10: first byte 100, second byte 64-127.
            if ($local[0] -eq 100 -and $local[1] -ge 64 -and $local[1] -le 127) {
                $result.TailnetAddress = $unicast.Address.ToString()
                continue
            }

            $mask = $null
            try { $mask = $unicast.IPv4Mask } catch { $mask = $null }
            if ($null -eq $mask) { continue }
            $maskBytes = $mask.GetAddressBytes()

            $sameSubnet = $true
            for ($i = 0; $i -lt 4; $i++) {
                if (($local[$i] -band $maskBytes[$i]) -ne ($vipBytes[$i] -band $maskBytes[$i])) { $sameSubnet = $false; break }
            }
            if ($sameSubnet -and -not $result.OnVipSubnet) {
                $result.OnVipSubnet = $true
                $result.VipSubnetDetail = "$($unicast.Address) on $($nic.Name)"
            }
        }
    }

    return $result
}

function New-DnsQuery {
    <#
    .SYNOPSIS
        One DNS query message for an A record, recursion desired.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$Id
    )
    $bytes = New-Object Collections.Generic.List[byte]
    $bytes.Add([byte](($Id -shr 8) -band 0xFF))
    $bytes.Add([byte]($Id -band 0xFF))
    $bytes.AddRange([byte[]]@(0x01, 0x00))              # flags: standard query, RD
    $bytes.AddRange([byte[]]@(0x00, 0x01))              # QDCOUNT
    $bytes.AddRange([byte[]]@(0x00, 0x00))              # ANCOUNT
    $bytes.AddRange([byte[]]@(0x00, 0x00))              # NSCOUNT
    $bytes.AddRange([byte[]]@(0x00, 0x00))              # ARCOUNT
    foreach ($label in $Name.TrimEnd('.').Split('.')) {
        $encoded = [Text.Encoding]::ASCII.GetBytes($label)
        $bytes.Add([byte]$encoded.Length)
        $bytes.AddRange($encoded)
    }
    $bytes.Add([byte]0)                                 # root label
    $bytes.AddRange([byte[]]@(0x00, 0x01))              # QTYPE A
    $bytes.AddRange([byte[]]@(0x00, 0x01))              # QCLASS IN

    # The leading comma keeps this a byte[] - see Test-NameResolution.ps1's
    # copy for the overload-resolution argument.
    return ,$bytes.ToArray()
}

function Skip-DnsName {
    <#
    .SYNOPSIS
        The offset just past a DNS name, without decoding it - a length-
        prefixed label sequence, terminated either by a zero byte or by a
        two-byte compression pointer (top two bits set).
    #>
    param(
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][int]$Offset
    )
    while ($Offset -lt $Buffer.Length) {
        $length = $Buffer[$Offset]
        if ($length -eq 0) { return $Offset + 1 }
        if (($length -band 0xC0) -eq 0xC0) { return $Offset + 2 }
        $Offset += 1 + $length
    }
    return $Offset
}

function Resolve-ViaServer {
    <#
    .SYNOPSIS
        Every A record one named resolver returns for a name, asked directly
        over UDP/53 - the client's cache and hosts file are not in this path.
        Lifted from Test-NameResolution.ps1, which carries the full account.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Server
    )

    $result = [pscustomobject]@{ Addresses = @(); Error = $null; Rcode = $null }
    $client = $null
    try {
        $id = Get-Random -Minimum 1 -Maximum 65535
        $query = New-DnsQuery -Name $Name -Id $id

        $client = New-Object Net.Sockets.UdpClient
        $client.Client.ReceiveTimeout = $script:TimeoutMs
        $client.Client.SendTimeout = $script:TimeoutMs
        $client.Connect($Server, 53)
        [void]$client.Send($query, $query.Length)

        $remote = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $response = $client.Receive([ref]$remote)

        if ($response.Length -lt 12) { $result.Error = "a $($response.Length)-byte reply, which is shorter than a DNS header"; return $result }
        # [int] casts on every shifted byte: PowerShell's -shl returns the
        # *left operand's* type, so [byte]0x36 -shl 8 is 0 rather than 13824
        # and every two-byte field silently collapses to its low half.
        if (((([int]$response[0]) -shl 8) -bor $response[1]) -ne $id) { $result.Error = 'the reply carried a different transaction id than the query'; return $result }

        $rcode = $response[3] -band 0x0F
        $result.Rcode = $rcode
        if ($rcode -ne 0) {
            $meaning = switch ($rcode) { 1 { 'FORMERR' } 2 { 'SERVFAIL' } 3 { 'NXDOMAIN' } 4 { 'NOTIMP' } 5 { 'REFUSED' } default { "rcode $rcode" } }
            $result.Error = "the resolver answered $meaning"
            return $result
        }
        if (($response[2] -band 0x02) -ne 0) { $result.Error = 'the reply was truncated (TC set), which seven A records should never be'; return $result }

        $answerCount = (([int]$response[6]) -shl 8) -bor $response[7]
        $offset = 12
        $offset = Skip-DnsName -Buffer $response -Offset $offset
        $offset += 4    # QTYPE + QCLASS

        $addresses = New-Object Collections.Generic.List[string]
        for ($i = 0; $i -lt $answerCount; $i++) {
            if ($offset + 10 -gt $response.Length) { break }
            $offset = Skip-DnsName -Buffer $response -Offset $offset
            if ($offset + 10 -gt $response.Length) { break }
            $type = (([int]$response[$offset]) -shl 8) -bor $response[$offset + 1]
            $class = (([int]$response[$offset + 2]) -shl 8) -bor $response[$offset + 3]
            $rdLength = (([int]$response[$offset + 8]) -shl 8) -bor $response[$offset + 9]
            $offset += 10
            if ($type -eq 1 -and $class -eq 1 -and $rdLength -eq 4 -and ($offset + 4) -le $response.Length) {
                $addresses.Add("$($response[$offset]).$($response[$offset + 1]).$($response[$offset + 2]).$($response[$offset + 3])")
            }
            $offset += $rdLength
        }

        $result.Addresses = @($addresses)
        if ($result.Addresses.Count -eq 0) { $result.Error = 'NOERROR with no A record in the answer section' }
        return $result
    }
    catch [Net.Sockets.SocketException] {
        $result.Error = "no answer from ${Server}:53 within ${TimeoutSeconds}s ($($_.Exception.SocketErrorCode))"
        return $result
    }
    catch {
        $result.Error = Get-ExceptionMessage $_
        return $result
    }
    finally {
        if ($client) { $client.Close() }
    }
}

function Resolve-ViaClient {
    <#
    .SYNOPSIS
        Every A record this client's own stack returns for a name - the OS
        cache and the hosts file included, on purpose: this is what an
        application on this machine actually gets.
    #>
    param([Parameter(Mandatory)][string]$Name)

    $result = [pscustomobject]@{ Addresses = @(); Error = $null }
    try {
        $addresses = @([Net.Dns]::GetHostAddresses($Name) |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
            ForEach-Object { $_.ToString() })
        $result.Addresses = $addresses
        if ($addresses.Count -eq 0) { $result.Error = 'resolved, but to no IPv4 address' }
    }
    catch {
        $result.Error = Get-ExceptionMessage $_
    }
    return $result
}

function Get-CertificateDnsNames {
    <#
    .SYNOPSIS
        The DNS names out of a certificate's subjectAltName extension.

    .DESCRIPTION
        Both spellings are parsed: Windows renders `DNS Name=host`, OpenSSL
        (so a macOS or Linux client) renders `DNS:host`. The second pass
        handles a localised Windows, where the label is translated but the
        `<label><separator><value>` shape is not; it only runs when the first
        found nothing. Lifted from Test-NameResolution.ps1.
    #>
    param([Parameter(Mandatory)][AllowNull()]$Certificate)

    $names = New-Object Collections.Generic.List[string]
    if ($null -eq $Certificate) { return @($names) }

    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne '2.5.29.17') { continue }
        $formatted = $extension.Format($false)

        foreach ($match in [regex]::Matches($formatted, '(?:DNS Name|dNSName|DNS)\s*[:=]\s*([^\s,]+)')) {
            $value = $match.Groups[1].Value.Trim()
            if ($value -and $names -notcontains $value) { $names.Add($value) }
        }
        if ($names.Count -gt 0) { continue }

        foreach ($part in ($formatted -split "[,\r\n]")) {
            $separator = [Math]::Max($part.LastIndexOf('='), $part.LastIndexOf(':'))
            if ($separator -lt 0) { continue }
            $value = $part.Substring($separator + 1).Trim()
            if ($value -and $names -notcontains $value) { $names.Add($value) }
        }
    }
    return @($names)
}

function Test-ProductionCertificate {
    <#
    .SYNOPSIS
        True when a certificate is the live wildcard 3b.10 bought - not
        staging, not expired, and covering the given host - with the reason
        it is not appended to $Reason when it fails.
    #>
    param(
        [Parameter(Mandatory)][AllowNull()]$Certificate,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][string]$HostName,
        [ref]$Reason
    )
    if ($null -eq $Certificate) { $Reason.Value = 'the handshake completed without a peer certificate'; return $false }
    $problems = New-Object Collections.Generic.List[string]
    $issuer = $Certificate.Issuer
    $names = Get-CertificateDnsNames $Certificate
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

function Invoke-HttpsHead {
    <#
    .SYNOPSIS
        One GET to a hostname - dialled *by name*, so the OS resolves it the
        way a browser would - returning the status code, the certificate, and
        the address the socket actually connected to.

    .DESCRIPTION
        The remote endpoint read back off the connected socket is this
        function's reason for existing: it is curl's `%{remote_ip}`, and it
        is the only value in this script that proves the cluster answered
        rather than something else that happens to hold a certificate.

        Only the status line is read; the body is never touched, so chunked
        encoding never has to be understood here. Certificate validation is
        accepted unconditionally - the certificate is the thing under
        inspection, not a precondition of the request completing.
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [string]$Path = '/',
        [int]$Port = 443
    )

    $result = [pscustomobject]@{
        Connected     = $false
        StatusCode    = $null
        RemoteAddress = $null
        Certificate   = $null
        Error         = $null
    }

    $client = $null
    $ssl = $null
    try {
        $client = New-Object Net.Sockets.TcpClient
        $connect = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($script:TimeoutMs)) {
            $result.Error = "no answer on ${HostName}:$Port within ${TimeoutSeconds}s"
            return $result
        }
        $client.EndConnect($connect)

        # Normalised back to dotted-quad before it is compared to anything:
        # TcpClient opens a dual-mode socket on .NET Core, so the peer of an
        # ordinary IPv4 connection reads back as ::ffff:10.0.0.30 and a string
        # comparison against INGRESS_VIP would fail every single time - the
        # check would look rigorous and be permanently red.
        $peer = ([Net.IPEndPoint]$client.Client.RemoteEndPoint).Address
        if ($peer.IsIPv4MappedToIPv6) { $peer = $peer.MapToIPv4() }
        $result.RemoteAddress = $peer.ToString()

        $accept = [Net.Security.RemoteCertificateValidationCallback] { param($channel, $peerCertificate, $chain, $policyErrors) return $true }
        $ssl = New-Object Net.Security.SslStream($client.GetStream(), $false, $accept)
        $ssl.ReadTimeout = $script:TimeoutMs
        $ssl.WriteTimeout = $script:TimeoutMs

        $protocols = [Security.Authentication.SslProtocols]::Tls12
        try { $protocols = $protocols -bor [Security.Authentication.SslProtocols]::Tls13 } catch { }
        $ssl.AuthenticateAsClient($HostName, $null, $protocols, $false)

        if ($ssl.RemoteCertificate) {
            $result.Certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        }

        $writer = New-Object IO.StreamWriter($ssl, (New-Object Text.ASCIIEncoding))
        $writer.NewLine = "`r`n"
        $writer.AutoFlush = $true
        $writer.WriteLine("GET $Path HTTP/1.1")
        $writer.WriteLine("Host: $HostName")
        $writer.WriteLine('User-Agent: aerie-test-cutover')
        $writer.WriteLine('Connection: close')
        $writer.WriteLine()

        $reader = New-Object IO.StreamReader($ssl, [Text.Encoding]::ASCII)
        $statusLine = $reader.ReadLine()
        if ($statusLine -match '^HTTP/\d\.\d\s+(\d{3})') { $result.StatusCode = [int]$Matches[1] }
        else { $result.Error = "the first line back was not an HTTP status line: '$statusLine'"; return $result }

        $result.Connected = $true
        return $result
    }
    catch {
        if (-not $result.Error) { $result.Error = Get-ExceptionMessage $_ }
        return $result
    }
    finally {
        if ($ssl) { $ssl.Dispose() }
        if ($client) { $client.Close() }
    }
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$step = '7c.11'

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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-cutover-gate'
            $privateKeyPath = $resolvedKey.Path
            $tempKeyFile = $resolvedKey.TempFile
            $keyFingerprint = $resolvedKey.Fingerprint
        }
        catch { $failures.Add($_.Exception.Message) }
    }

    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        $failures.Add("$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right.")
    }

    # The Tree stage reads a checkout rather than the cluster, and a wrong
    # root would pass every deletion check by looking at a directory that
    # never held the files. Anchored on two things this repository always
    # has, so a stale or half-copied tree is a usage error here rather than
    # a green table later.
    # The Tree stage reads the *commit*, not the working tree - see its own
    # comment for why. All this has to establish is that there is a commit to
    # read: a git that runs, a repository under -RepositoryRoot, and a HEAD
    # that carries the four directories 7c.11's last two bullets are about.
    $repositoryRootPath = $null
    $treeFiles = @()
    if (Test-Path $RepositoryRoot -PathType Container) {
        $repositoryRootPath = (Resolve-Path $RepositoryRoot).Path

        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            $failures.Add('git is not on PATH. The tree checks read the committed tree rather than the working directory, so they cannot run without it.')
        }
        else {
            $listing = Invoke-Git -RepositoryRoot $repositoryRootPath -Arguments @('ls-tree', '-r', '--name-only', 'HEAD')
            if ($listing.ExitCode -ne 0) {
                $failures.Add("git could not read HEAD under '$repositoryRootPath': $((($listing.StdErr) -replace '\s+', ' ').Trim())")
            }
            else {
                $treeFiles = @($listing.StdOut -split "`r?`n" | Where-Object { $_ })
                $missingAnchors = @(@('deploy/', 'charts/', 'scripts/', '.github/') | Where-Object { $anchor = $_; -not ($treeFiles | Where-Object { $_.StartsWith($anchor) } | Select-Object -First 1) })
                if ($missingAnchors.Count -gt 0) {
                    $failures.Add("HEAD under '$repositoryRootPath' contains no $($missingAnchors -join ', ') - $($treeFiles.Count) file(s) in the commit, so this is not an Aerie checkout.")
                }
            }
        }
    }
    else {
        $failures.Add("-RepositoryRoot '$RepositoryRoot' is not a directory.")
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Cluster:    $IPAddress"
    Write-Host "Repository: $repositoryRootPath"
    if ($keyFingerprint) { Write-Host "SSH key:    $keyFingerprint" }
    Write-Host 'Preflight OK. Everything below is read-only: `kubectl get`, one proxied GET to Prometheus, DNS queries and TLS handshakes.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-cutover-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip for every object this script reasons about. Every
    # command falls back to `echo {}` on an unregistered type, a missing
    # object or a proxied 4xx/5xx, single quotes only - Invoke-NodeSsh
    # refuses a command containing a double quote (Windows PowerShell 5.1
    # would otherwise strip it en route).
    #
    # PVCs rather than Longhorn's own kubernetesStatus for the r3 mapping:
    # spec.storageClassName is what asked for three replicas and
    # spec.volumeName is the Longhorn volume that answered, both on the same
    # object, so the join needs nothing from Longhorn's view of Kubernetes.
    $probeScript = @(
        'printf ''\n--- clusterconfig\n'''
        'sudo k3s kubectl -n flux-system get configmap aerie-cluster-config -o json 2>/dev/null || echo {}'
        'printf ''\n--- nodes\n'''
        'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
        'printf ''\n--- pvcs\n'''
        'sudo k3s kubectl get pvc --all-namespaces -o json 2>/dev/null || echo {}'
        'printf ''\n--- lhvolumes\n'''
        'sudo k3s kubectl -n longhorn-system get volumes.longhorn.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- lhreplicas\n'''
        'sudo k3s kubectl -n longhorn-system get replicas.longhorn.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- pgcluster\n'''
        'sudo k3s kubectl -n aerie get cluster.postgresql.cnpg.io aerie-pg -o json 2>/dev/null || echo {}'
        'printf ''\n--- pgpods\n'''
        'sudo k3s kubectl -n aerie get pods -l cnpg.io/cluster=aerie-pg -o json 2>/dev/null || echo {}'
        'printf ''\n--- pgbackups\n'''
        'sudo k3s kubectl -n aerie get backups.postgresql.cnpg.io -o json 2>/dev/null || echo {}'
        'printf ''\n--- apideploy\n'''
        'sudo k3s kubectl -n aerie get deployment api -o json 2>/dev/null || echo {}'
        'printf ''\n--- apipods\n'''
        'sudo k3s kubectl -n aerie get pods -l app.kubernetes.io/component=api -o json 2>/dev/null || echo {}'
        'printf ''\n--- promtargets\n'''
        "sudo k3s kubectl get --raw '/api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/targets?state=active' 2>/dev/null || echo {}"
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
    $pvcs = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pvcs'))
    $longhornVolumes = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'lhvolumes'))
    $longhornReplicas = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'lhreplicas'))
    $pgCluster = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pgcluster'
    $pgPods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pgpods'))
    $pgBackups = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pgbackups'))
    $apiDeployment = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'apideploy'
    $apiPods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'apipods'))
    $promTargets = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'promtargets'
    $activeTargets = @(Get-Path $promTargets 'data.activeTargets' | Where-Object { $_ })

    # DOMAIN and INGRESS_VIP are the expectation the Resolution and Fetch
    # stages test DNS against, and they come from the cluster - the same
    # ConfigMap provision-4-cluster-config.yml renders from vars.DOMAIN and
    # vars.INGRESS_VIP. Only the *answer* is allowed to come from DNS.
    $domain = [string](Get-MapValue $clusterConfig 'DOMAIN')
    $ingressVip = [string](Get-MapValue $clusterConfig 'INGRESS_VIP')
    if ($domain) { $domain = $domain.Trim().TrimEnd('.').ToLowerInvariant() }
    if ($ingressVip) { $ingressVip = $ingressVip.Trim() }

    # Node name -> InternalIP, and the reverse. Every "on three distinct
    # nodes" check below counts names; the etcd and Prometheus checks match
    # on addresses.
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

    # ---------------------------------------------------------------- #
    Write-Stage 'Nodes'
    # ---------------------------------------------------------------- #

    # etcd membership is a property of each machine rather than of the
    # control plane, so it is asked of each machine. Three things per node,
    # and they fail for different reasons on purpose: the endpoint answering
    # at all is 6b.1's etcd-expose-metrics; `has_leader` is whether this
    # member is in a working quorum; and the peer count is whether it can see
    # the *other two* - a member that rejoined into a two-member view has a
    # leader and is still not what 7c.7 asked for.
    $etcdLeaderCount = 0
    $etcdReporting = 0
    if ($nodeAddresses.Count -eq 0) {
        Add-Check -Step $step -Name 'etcd on every node (7c.7)' -Status 'Fail' -Detail 'no node reported an InternalIP, so no node could be reached'
    }

    foreach ($node in $nodeAddresses) {
        $label = "$($node.Name) ($($node.Address))"

        if (-not (Test-TcpPort -IPAddress $node.Address -Port 22)) {
            Add-Check -Step $step -Name "etcd member on $($node.Name) (7c.7)" -Status 'Fail' -Detail "$($node.Address) is not answering on port 22 from here"
            continue
        }

        $nodeSsh = @{ IPAddress = $node.Address; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }
        $nodeProbe = Invoke-NodeSsh @nodeSsh -ConnectTimeoutSec 20 -Command (@(
                'echo ''--- etcdcode'''
                # -w's trailing \n is load-bearing: without it curl writes the
                # status code with no line terminator, it runs into the next
                # marker, and Get-ProbeSection hands back '200--- etcdleader'.
                'curl -s --max-time 5 -o /dev/null -w ''%{http_code}\n'' http://127.0.0.1:2381/metrics 2>/dev/null || echo 000'
                'echo ''--- etcdleader'''
                'curl -s --max-time 5 http://127.0.0.1:2381/metrics 2>/dev/null | grep -E ''^etcd_server_(has|is)_leader '' || true'
                'echo ''--- etcdpeers'''
                # The label is To=<quote><id><quote>; [^,}]+ swallows the
                # quotes without this command having to contain one, which
                # Invoke-NodeSsh refuses.
                'curl -s --max-time 5 http://127.0.0.1:2381/metrics 2>/dev/null | grep -oE ''etcd_network_peer_sent_bytes_total\{To=[^,}]+'' | sort -u | wc -l'
                'echo ''--- end'''
            ) -join '; ')

        if ($nodeProbe.ExitCode -ne 0) {
            Add-Check -Step $step -Name "etcd member on $($node.Name) (7c.7)" -Status 'Fail' -Detail "SSH to $label failed: $(($nodeProbe.StdErr -replace '\s+', ' ').Trim())"
            continue
        }

        $etcdCode = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'etcdcode').Trim()
        if ($etcdCode -eq '200') {
            Add-Check -Step $step -Name "etcd metrics endpoint (:2381) on $($node.Name)" -Status 'Pass' -Detail '200'
        }
        else {
            Add-Check -Step $step -Name "etcd metrics endpoint (:2381) on $($node.Name)" -Status 'Fail' -Detail "HTTP $etcdCode - confirm etcd-expose-metrics: true (scripts/k3s/Install-K3sNode.ps1, 6b.1) and that k3s restarted after it was set"
            continue
        }

        $leaderLines = @((Get-ProbeSection -Output $nodeProbe.StdOut -Name 'etcdleader') -split "`n" | Where-Object { $_ })
        $hasLeader = $null
        $isLeader = $null
        foreach ($line in $leaderLines) {
            if ($line -match '^etcd_server_has_leader\s+(\S+)') { $hasLeader = $Matches[1] }
            if ($line -match '^etcd_server_is_leader\s+(\S+)') { $isLeader = $Matches[1] }
        }
        if ($isLeader -eq '1') { $etcdLeaderCount++ }
        if ($null -ne $hasLeader) { $etcdReporting++ }

        if ($hasLeader -eq '1') {
            Add-Check -Step $step -Name "etcd member on $($node.Name) has a leader" -Status 'Pass' -Detail $(if ($isLeader -eq '1') { 'this member is the leader' } else { 'following' })
        }
        elseif ($null -eq $hasLeader) {
            Add-Check -Step $step -Name "etcd member on $($node.Name) has a leader" -Status 'Fail' -Detail ':2381 answered 200 but exposed no etcd_server_has_leader - that is not an etcd metrics endpoint'
        }
        else {
            Add-Check -Step $step -Name "etcd member on $($node.Name) has a leader" -Status 'Fail' -Detail "etcd_server_has_leader is $hasLeader - this member is not in a working quorum"
        }

        $peerText = (Get-ProbeSection -Output $nodeProbe.StdOut -Name 'etcdpeers').Trim()
        $peerCount = 0
        [void][int]::TryParse($peerText, [ref]$peerCount)
        $expectedPeers = $nodeAddresses.Count - 1
        if ($peerCount -eq $expectedPeers) {
            Add-Check -Step $step -Name "etcd member on $($node.Name) sees $expectedPeers peer(s)" -Status 'Pass' -Detail "$peerCount of $expectedPeers"
        }
        else {
            Add-Check -Step $step -Name "etcd member on $($node.Name) sees $expectedPeers peer(s)" -Status 'Fail' -Detail "sees $peerCount - a member with a leader and the wrong peer count is in a smaller cluster than $($nodeAddresses.Count) nodes"
        }
    }

    if ($etcdReporting -gt 0) {
        if ($etcdLeaderCount -eq 1) {
            Add-Check -Step $step -Name 'Exactly one etcd leader across the members' -Status 'Pass' -Detail "1 of $etcdReporting reporting members"
        }
        else {
            Add-Check -Step $step -Name 'Exactly one etcd leader across the members' -Status 'Fail' -Detail "$etcdLeaderCount member(s) report etcd_server_is_leader 1, out of $etcdReporting reporting"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- 7c.7: three nodes, all Ready ---------------------------------
    if ($nodes.Count -eq 0) {
        Add-Check -Step $step -Name 'Three nodes Ready (7c.7)' -Status 'Fail' -Detail 'kubectl returned no nodes at all'
    }
    else {
        $notReady = New-Object Collections.Generic.List[string]
        $cordoned = New-Object Collections.Generic.List[string]
        foreach ($node in $nodes) {
            $name = [string](Get-Path $node 'metadata.name')
            $ready = @(Get-Path $node 'status.conditions' | Where-Object { (Get-Field $_ 'type') -eq 'Ready' } | Select-Object -First 1)
            $status = if ($ready.Count -gt 0) { [string](Get-Field $ready[0] 'status') } else { 'absent' }
            if ($status -ne 'True') { $notReady.Add("${name}: Ready=$status") }
            if ((Get-Path $node 'spec.unschedulable') -eq $true) { $cordoned.Add($name) }
        }
        if ($notReady.Count -gt 0) {
            Add-Check -Step $step -Name 'Three nodes Ready (7c.7)' -Status 'Fail' -Detail ($notReady -join '; ')
        }
        elseif ($nodes.Count -ne 3) {
            # Not "at least three": three is what design.md's quorum argument
            # asks for and what POSTGRES_INSTANCES and longhorn-r3 are sized
            # against. A fourth node is a new decision, not a better result.
            Add-Check -Step $step -Name 'Three nodes Ready (7c.7)' -Status 'Fail' -Detail "$($nodes.Count) node(s) Ready, not 3: $(($nodeAddresses | ForEach-Object { $_.Name }) -join ', ')"
        }
        else {
            Add-Check -Step $step -Name 'Three nodes Ready (7c.7)' -Status 'Pass' -Detail (($nodeAddresses | ForEach-Object { "$($_.Name) ($($_.Address))" }) -join ', ')
        }
        if ($cordoned.Count -gt 0) {
            Add-Check -Step $step -Name 'No node is cordoned' -Status 'Fail' -Detail "$($cordoned -join ', ') - Ready but unschedulable, which the pod-spread checks below would pass anyway on pods placed before it"
        }
    }

    # --- 7c.7: Prometheus scrapes every etcd member --------------------
    # The scrape pool is matched rather than the job label: which label
    # kube-prometheus-stack assigns is a chart convention this script does
    # not trust itself to predict, and the pool name carries the object that
    # created it. A short target list here is the K3S_SERVER_ADDRESSES half
    # of 7c.7 - kube-prometheus-stack.yaml's kubeEtcd.endpoints is a static
    # list rendered from that variable, discovered by nothing, so a third
    # server that joined without it being raised is monitored on two members.
    $etcdTargets = @($activeTargets | Where-Object { [string](Get-Field $_ 'scrapePool') -like '*kube-etcd*' })
    if ($activeTargets.Count -eq 0) {
        Add-Check -Step $step -Name 'Prometheus scrapes every etcd member (7c.7)' -Status 'Fail' -Detail 'Prometheus reported no active targets at all - it is not reachable through the service proxy, or has discovered nothing'
    }
    elseif ($etcdTargets.Count -eq 0) {
        Add-Check -Step $step -Name 'Prometheus scrapes every etcd member (7c.7)' -Status 'Fail' -Detail 'no active target in a *kube-etcd* scrape pool - kubeEtcd (deploy/cluster/observability/controllers/kube-prometheus-stack.yaml) is disabled or its Endpoints object is empty'
    }
    else {
        $etcdInstances = @($etcdTargets | ForEach-Object { [string](Get-Path $_ 'labels.instance') } | Where-Object { $_ })
        $missing = @($nodeAddresses | Where-Object { $address = $_.Address; -not (@($etcdInstances) | Where-Object { $_ -like "${address}:*" }) })
        $down = @($etcdTargets | Where-Object { (Get-Field $_ 'health') -ne 'up' } | ForEach-Object { "$(Get-Path $_ 'labels.instance'): $(Get-Field $_ 'lastError')" })
        if ($missing.Count -gt 0) {
            Add-Check -Step $step -Name 'Prometheus scrapes every etcd member (7c.7)' -Status 'Fail' `
                -Detail "no etcd target for $((($missing | ForEach-Object { "$($_.Name) ($($_.Address))" }) -join ', ')) - raise K3S_SERVER_ADDRESSES to all $($nodeAddresses.Count) server addresses and re-dispatch Provision 4; scraping $($etcdInstances -join ', ')"
        }
        elseif ($down.Count -gt 0) {
            Add-Check -Step $step -Name 'Prometheus scrapes every etcd member (7c.7)' -Status 'Fail' -Detail ($down -join '; ')
        }
        else {
            Add-Check -Step $step -Name 'Prometheus scrapes every etcd member (7c.7)' -Status 'Pass' -Detail "$($etcdInstances.Count) member(s) up: $($etcdInstances -join ', ')"
        }
    }

    # --- 7c.8: both counts are 3 in the live ConfigMap -----------------
    # The ConfigMap rather than the repository variable, because the
    # ConfigMap is what the cluster reads: a variable raised and never
    # pushed through Provision 4 is the exact failure this catches.
    foreach ($key in @('LONGHORN_REPLICA_COUNT', 'POSTGRES_INSTANCES')) {
        $value = [string](Get-MapValue $clusterConfig $key)
        if (-not $value) {
            Add-Check -Step $step -Name "$key is 3 (7c.8)" -Status 'Fail' -Detail 'not present in the live aerie-cluster-config'
        }
        elseif ($value.Trim() -eq '3') {
            Add-Check -Step $step -Name "$key is 3 (7c.8)" -Status 'Pass' -Detail '3'
        }
        else {
            Add-Check -Step $step -Name "$key is 3 (7c.8)" -Status 'Fail' -Detail "reads '$value' - raise the repository variable and re-dispatch Provision 4"
        }
    }

    # --- 7c.8/7c.9: aerie-pg, three instances, three nodes, required ---
    if ($null -eq $pgCluster -or -not (Get-Path $pgCluster 'metadata.name')) {
        Add-Check -Step $step -Name 'Cluster aerie-pg has 3 instances (7c.8)' -Status 'Fail' -Detail 'not found in namespace aerie'
    }
    else {
        $readyInstances = [string](Get-Path $pgCluster 'status.readyInstances')
        $specInstances = [string](Get-Path $pgCluster 'spec.instances')
        $phase = [string](Get-Path $pgCluster 'status.phase')
        if ($readyInstances -eq '3' -and $specInstances -eq '3' -and $phase -eq 'Cluster in healthy state') {
            Add-Check -Step $step -Name 'Cluster aerie-pg has 3 instances (7c.8)' -Status 'Pass' -Detail "3/3 ready, phase: $phase"
        }
        else {
            Add-Check -Step $step -Name 'Cluster aerie-pg has 3 instances (7c.8)' -Status 'Fail' -Detail "spec.instances=$specInstances, readyInstances=$readyInstances (want 3 and 3), phase: $phase"
        }

        # 4b.6's podAntiAffinityType: required is what makes this a test of
        # the third node rather than of a number: with two nodes the third
        # pod had nowhere to go.
        if ($pgPods.Count -eq 0) {
            Add-Check -Step $step -Name 'aerie-pg instances on 3 distinct nodes (7c.8)' -Status 'Fail' -Detail 'no instance pods found (label cnpg.io/cluster=aerie-pg)'
        }
        else {
            $pgNodes = @($pgPods | ForEach-Object { [string](Get-Path $_ 'spec.nodeName') } | Where-Object { $_ })
            $pgDistinct = @($pgNodes | Select-Object -Unique)
            if ($pgNodes.Count -ne $pgPods.Count) {
                Add-Check -Step $step -Name 'aerie-pg instances on 3 distinct nodes (7c.8)' -Status 'Fail' -Detail "$($pgPods.Count - $pgNodes.Count) instance pod(s) not scheduled (no spec.nodeName) - with required anti-affinity that is a pod with nowhere to go"
            }
            elseif ($pgDistinct.Count -eq 3 -and $pgPods.Count -eq 3) {
                Add-Check -Step $step -Name 'aerie-pg instances on 3 distinct nodes (7c.8)' -Status 'Pass' -Detail ($pgDistinct -join ', ')
            }
            else {
                Add-Check -Step $step -Name 'aerie-pg instances on 3 distinct nodes (7c.8)' -Status 'Fail' -Detail "$($pgPods.Count) pod(s) across $($pgDistinct.Count) node(s): $($pgNodes -join ', ')"
            }
        }

        # 7c.9's headline: RPO=0. `preferred` degrades to asynchronous
        # replication when the synchronous standby is away, which is the
        # two-node compromise this phase was allowed to end.
        $durability = [string](Get-Path $pgCluster 'spec.postgresql.synchronous.dataDurability')
        if ($durability -eq 'required') {
            Add-Check -Step $step -Name 'synchronous.dataDurability is required (7c.9)' -Status 'Pass' -Detail "required, number: $([string](Get-Path $pgCluster 'spec.postgresql.synchronous.number'))"
        }
        else {
            Add-Check -Step $step -Name 'synchronous.dataDurability is required (7c.9)' -Status 'Fail' -Detail "reads '$durability' - deploy/cluster/data/cluster/cluster.yaml still carries the two-node compromise, or the commit has not reconciled"
        }
    }

    # --- 4b.10 through 7b.2: the newest Backup, which is now the only one
    # The schedule comes from the committed manifest rather than a
    # parameter, the same source Test-DataTier.ps1 reads it from.
    $scheduleExpression = Get-YamlScalar -Path $ScheduledBackupManifestPath -Key 'schedule'
    if ($pgBackups.Count -eq 0) {
        Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Fail' -Detail 'no Backup objects in namespace aerie - after 7b.2 this is the only backup the system has'
    }
    elseif (-not $scheduleExpression) {
        Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Fail' -Detail "could not read a schedule out of $ScheduledBackupManifestPath, so 'younger than the interval' has no interval to test against"
    }
    else {
        # Sorted on the real instant, not on its rendering - see
        # ConvertTo-UtcDateTime for what a [string] cast does to both the
        # order and the age. A Backup that never started sorts as MinValue,
        # which puts it last descending: correct, since it is not a candidate
        # for "the newest completed one".
        $newest = $pgBackups |
            Sort-Object { $moment = ConvertTo-UtcDateTime (Get-Path $_ 'status.startedAt'); if ($null -eq $moment) { [DateTime]::MinValue } else { $moment } } -Descending |
            Select-Object -First 1
        $backupPhase = [string](Get-Path $newest 'status.phase')
        $startedAtRaw = Get-Path $newest 'status.startedAt'
        $started = ConvertTo-UtcDateTime $startedAtRaw
        if ($backupPhase -ne 'completed') {
            Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Fail' -Detail "phase is '$backupPhase', not completed - $(Get-Path $newest 'metadata.name')"
        }
        elseif ($null -eq $started) {
            Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Fail' -Detail "completed, but status.startedAt ('$startedAtRaw') is not a timestamp"
        }
        else {
            $age = (Get-Date).ToUniversalTime() - $started
            $cronFields = @($scheduleExpression -split '\s+' | Where-Object { $_ })
            if ($cronFields.Count -eq 6 -and $cronFields[3] -eq '*' -and $cronFields[4] -eq '*' -and $cronFields[5] -eq '*') {
                if ($age.TotalHours -le 25) {
                    Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Pass' -Detail "$(Get-Path $newest 'metadata.name') completed $([math]::Round($age.TotalHours, 1))h ago (daily schedule)"
                }
                else {
                    Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Fail' -Detail "completed, but $([math]::Round($age.TotalHours, 1))h old against a daily ('$scheduleExpression') schedule"
                }
            }
            else {
                Add-Check -Step $step -Name 'Newest CNPG Backup completed and recent (7b.2)' -Status 'Warn' -Detail "completed $([math]::Round($age.TotalHours, 1))h ago; schedule '$scheduleExpression' is not a plain daily expression this gate knows how to age-check"
            }
        }
    }

    # --- 7c.8: Longhorn --------------------------------------------------
    # Two separate questions. "Nothing is Degraded" is the whole cluster's
    # storage health. "Every longhorn-r3 volume has three replicas on three
    # distinct nodes" is the one the third node was supposed to fix, and
    # Healthy alone does not answer it: a volume whose spec was never raised
    # is Healthy at two replicas.
    if ($longhornVolumes.Count -eq 0) {
        Add-Check -Step $step -Name 'No Longhorn volume is Degraded (7c.8)' -Status 'Fail' -Detail 'no Longhorn volumes found at all - the CRD is unregistered or Longhorn is not installed'
    }
    else {
        $unhealthy = @($longhornVolumes |
                Where-Object { [string](Get-Path $_ 'status.robustness') -ne 'healthy' } |
                ForEach-Object { "$(Get-Path $_ 'metadata.name'): $(Get-Path $_ 'status.robustness')" })
        if ($unhealthy.Count -gt 0) {
            Add-Check -Step $step -Name 'No Longhorn volume is Degraded (7c.8)' -Status 'Fail' -Detail ($unhealthy -join '; ')
        }
        else {
            Add-Check -Step $step -Name 'No Longhorn volume is Degraded (7c.8)' -Status 'Pass' -Detail "$($longhornVolumes.Count) volume(s), all healthy"
        }
    }

    $r3Claims = @($pvcs | Where-Object { [string](Get-Path $_ 'spec.storageClassName') -eq 'longhorn-r3' })
    if ($r3Claims.Count -eq 0) {
        Add-Check -Step $step -Name 'Every longhorn-r3 volume has 3 replicas on 3 nodes (7c.8)' -Status 'Fail' `
            -Detail 'no PersistentVolumeClaim names longhorn-r3 - grafana, kuma and alertmanager (Phase 6) should, so either the class was renamed or nothing was read'
    }
    else {
        $r3Problems = New-Object Collections.Generic.List[string]
        $r3Good = New-Object Collections.Generic.List[string]
        foreach ($claim in $r3Claims) {
            $claimName = "$(Get-Path $claim 'metadata.namespace')/$(Get-Path $claim 'metadata.name')"
            $volumeName = [string](Get-Path $claim 'spec.volumeName')
            if (-not $volumeName) { $r3Problems.Add("${claimName}: unbound, no volume"); continue }

            $volume = $longhornVolumes | Where-Object { [string](Get-Path $_ 'metadata.name') -eq $volumeName } | Select-Object -First 1
            if ($null -eq $volume) { $r3Problems.Add("${claimName}: no Longhorn volume named $volumeName"); continue }

            $specReplicas = [string](Get-Path $volume 'spec.numberOfReplicas')
            # Running replicas only. A replica object with a failedAt or in
            # any state but running is scheduled, not carrying data, and
            # counting it is how a degraded volume passes a replica count.
            $replicaNodes = @($longhornReplicas |
                    Where-Object {
                        [string](Get-Path $_ 'spec.volumeName') -eq $volumeName -and
                        [string](Get-Path $_ 'status.currentState') -eq 'running' -and
                        -not [string](Get-Path $_ 'spec.failedAt')
                    } |
                    ForEach-Object { [string](Get-Path $_ 'spec.nodeID') } |
                    Where-Object { $_ })
            $distinctNodes = @($replicaNodes | Select-Object -Unique)

            if ($specReplicas -ne '3') {
                $r3Problems.Add("${claimName}: spec.numberOfReplicas is $specReplicas, not 3 - a volume's replica count is fixed at creation, so this one predates the class")
            }
            elseif ($distinctNodes.Count -ne 3) {
                $r3Problems.Add("${claimName}: $($replicaNodes.Count) running replica(s) on $($distinctNodes.Count) node(s) [$($distinctNodes -join ', ')]")
            }
            else {
                $r3Good.Add("$claimName on $($distinctNodes -join ', ')")
            }
        }
        if ($r3Problems.Count -gt 0) {
            Add-Check -Step $step -Name 'Every longhorn-r3 volume has 3 replicas on 3 nodes (7c.8)' -Status 'Fail' -Detail ($r3Problems -join '; ')
        }
        else {
            Add-Check -Step $step -Name 'Every longhorn-r3 volume has 3 replicas on 3 nodes (7c.8)' -Status 'Pass' -Detail "$($r3Claims.Count) volume(s): $($r3Good -join '; ')"
        }
    }

    # --- 7c.9: the api Deployment's spread and its rollout strategy -----
    # Both, in one place, because they are one decision: DoNotSchedule at
    # 3 replicas over 3 nodes is what makes maxSurge: 0 mandatory, and the
    # deadlock a missing strategy causes surfaces at the *next* image bump
    # rather than at the commit - which is what makes it worth a gate.
    if ($null -eq $apiDeployment -or -not (Get-Path $apiDeployment 'metadata.name')) {
        Add-Check -Step $step -Name 'api spread is DoNotSchedule (7c.9)' -Status 'Fail' -Detail 'Deployment api not found in namespace aerie'
        Add-Check -Step $step -Name 'api rollout strategy is maxSurge 0 (7c.9)' -Status 'Fail' -Detail 'Deployment api not found in namespace aerie'
    }
    else {
        $constraints = @(Get-Path $apiDeployment 'spec.template.spec.topologySpreadConstraints' | Where-Object { $_ })
        $hostnameConstraint = $constraints | Where-Object { [string](Get-Field $_ 'topologyKey') -eq 'kubernetes.io/hostname' } | Select-Object -First 1
        if ($null -eq $hostnameConstraint) {
            Add-Check -Step $step -Name 'api spread is DoNotSchedule (7c.9)' -Status 'Fail' -Detail "no topologySpreadConstraint on kubernetes.io/hostname at all ($($constraints.Count) constraint(s) present)"
        }
        elseif ([string](Get-Field $hostnameConstraint 'whenUnsatisfiable') -eq 'DoNotSchedule') {
            Add-Check -Step $step -Name 'api spread is DoNotSchedule (7c.9)' -Status 'Pass' -Detail "maxSkew $([string](Get-Field $hostnameConstraint 'maxSkew')), DoNotSchedule"
        }
        else {
            Add-Check -Step $step -Name 'api spread is DoNotSchedule (7c.9)' -Status 'Fail' -Detail "whenUnsatisfiable is '$([string](Get-Field $hostnameConstraint 'whenUnsatisfiable'))' - charts/aerie/templates/api-deployment.yaml still carries the two-node compromise"
        }

        $maxSurge = [string](Get-Path $apiDeployment 'spec.strategy.rollingUpdate.maxSurge')
        $maxUnavailable = [string](Get-Path $apiDeployment 'spec.strategy.rollingUpdate.maxUnavailable')
        if ($maxSurge -eq '0' -and $maxUnavailable -ne '0') {
            Add-Check -Step $step -Name 'api rollout strategy is maxSurge 0 (7c.9)' -Status 'Pass' -Detail "maxSurge 0, maxUnavailable $maxUnavailable"
        }
        else {
            Add-Check -Step $step -Name 'api rollout strategy is maxSurge 0 (7c.9)' -Status 'Fail' `
                -Detail "maxSurge '$maxSurge', maxUnavailable '$maxUnavailable' - with DoNotSchedule at 3 replicas over 3 nodes a surge pod is skew 2 wherever it lands, so the next image bump would wait forever for a pod that can never schedule"
        }
    }

    # --- 7c.9: one api pod per node -------------------------------------
    if ($apiPods.Count -eq 0) {
        Add-Check -Step $step -Name 'api pods are one per node (7c.9)' -Status 'Fail' -Detail 'no pods found (label app.kubernetes.io/component=api)'
    }
    else {
        $runningApiPods = @($apiPods | Where-Object { [string](Get-Path $_ 'status.phase') -eq 'Running' })
        $apiNodes = @($runningApiPods | ForEach-Object { [string](Get-Path $_ 'spec.nodeName') } | Where-Object { $_ })
        $apiDistinct = @($apiNodes | Select-Object -Unique)
        if ($runningApiPods.Count -ne $apiPods.Count) {
            $notRunning = @($apiPods | Where-Object { [string](Get-Path $_ 'status.phase') -ne 'Running' } | ForEach-Object { "$(Get-Path $_ 'metadata.name'): $(Get-Path $_ 'status.phase')" })
            Add-Check -Step $step -Name 'api pods are one per node (7c.9)' -Status 'Fail' -Detail ($notRunning -join '; ')
        }
        elseif ($apiDistinct.Count -eq $apiNodes.Count -and $apiDistinct.Count -eq $nodes.Count) {
            Add-Check -Step $step -Name 'api pods are one per node (7c.9)' -Status 'Pass' -Detail "$($apiNodes.Count) pod(s) on $($apiDistinct -join ', ')"
        }
        else {
            Add-Check -Step $step -Name 'api pods are one per node (7c.9)' -Status 'Fail' -Detail "$($apiNodes.Count) running pod(s) across $($apiDistinct.Count) node(s) of $($nodes.Count): $($apiNodes -join ', ')"
        }
    }

    # --- Finding 3: every Windows host is up, including the rebuilt one --
    # The check that catches 7c.4's omission. The pfSense reservation was
    # made against the old physical NIC's MAC; New-VMSwitch -AllowManagementOS
    # moves the host's traffic onto a vEthernet adapter with a new one, and
    # WINDOWS_EXPORTER_TARGETS is a static list discovered by nothing - so a
    # host that took a pool address is scraped at an address that answers for
    # someone else, and the symptom looks like a Prometheus fault a week later.
    $windowsTargetsRaw = [string](Get-MapValue $clusterConfig 'WINDOWS_EXPORTER_TARGETS')
    $windowsHosts = $null
    if (-not $windowsTargetsRaw) {
        Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail 'WINDOWS_EXPORTER_TARGETS is not set in the live aerie-cluster-config'
    }
    else {
        try {
            $parsed = @(ConvertTo-FlatArray ($windowsTargetsRaw | ConvertFrom-Json))
            $bad = @($parsed | Where-Object { $_ -notmatch '^[^:\s]+:\d+$' })
            if ($parsed.Count -eq 0) { Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail 'parsed to an empty list' }
            elseif ($bad.Count -gt 0) { Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail "not host:port - $($bad -join ', ')" }
            else { $windowsHosts = $parsed }
        }
        catch {
            Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail "not valid JSON: $($_.Exception.Message)"
        }
    }

    if ($windowsHosts) {
        $windowsTargets = @($activeTargets | Where-Object { [string](Get-Field $_ 'scrapePool') -like '*windows-exporter*' })
        $observed = @($windowsTargets | ForEach-Object { [string](Get-Path $_ 'labels.instance') } | Where-Object { $_ })
        $missingHosts = @($windowsHosts | Where-Object { $observed -notcontains $_ })
        $downHosts = @($windowsTargets | Where-Object { (Get-Field $_ 'health') -ne 'up' } | ForEach-Object { "$(Get-Path $_ 'labels.instance'): $(Get-Field $_ 'lastError')" })
        if ($windowsTargets.Count -eq 0) {
            Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail 'no active target in a *windows-exporter* scrape pool - the ScrapeConfig (6b.6) is missing or undiscovered'
        }
        elseif ($missingHosts.Count -gt 0) {
            Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' -Detail "no target for: $($missingHosts -join ', ') - present: $($observed -join ', ')"
        }
        elseif ($downHosts.Count -gt 0) {
            Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Fail' `
                -Detail "$($downHosts -join '; ') - if this is the rebuilt host, its DHCP reservation is against the old physical NIC's MAC rather than the vEthernet (ExternalSwitch) adapter's"
        }
        else {
            Add-Check -Step $step -Name 'All WINDOWS_EXPORTER_TARGETS are up (7c.4)' -Status 'Pass' -Detail "$($observed.Count) host(s) up: $($observed -join ', ')"
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Resolution'
    # ---------------------------------------------------------------- #

    # The seven, in the order 7b.6's own loop names them, each with the path
    # that gives a deterministic answer and the codes that count as "the
    # cluster answered". Three of the seven bounce an anonymous caller to a
    # login page; the status code is the weaker half of every row anyway -
    # the address is what is being proven, and the code is only here so that
    # a 502 from a healthy-looking Traefik is not read as a pass.
    # kiosk is the one row whose expected code depends on the installation
    # rather than on the cluster being healthy. At auth.mode full the wall
    # joins that route (charts/aerie/templates/ingress.yaml), so an anonymous
    # GET is refused - 401 to a plain client, 302 to a browser being sent to
    # the sign-in shell - and a 200 there would be the finding rather than
    # the pass. At none it is an ordinary 200. Read from the same ConfigMap
    # the chart's own auth.mode is rendered from, so this cannot disagree
    # with what is deployed.
    $authMode = [string](Get-MapValue $clusterConfig 'AUTH_MODE')
    if (-not $authMode) { $authMode = 'none' }
    $kioskExpect = if ($authMode -eq 'full') { @(401, 302) } else { @(200) }

    $sites = @()
    if ($domain) {
        $sites = @(
            [pscustomobject]@{ Name = "home.$domain";    Path = '/health/ready'; Expect = @(200) }
            [pscustomobject]@{ Name = "kiosk.$domain";   Path = '/';             Expect = $kioskExpect }
            [pscustomobject]@{ Name = "files.$domain";   Path = '/version.json'; Expect = @(200) }
            [pscustomobject]@{ Name = "share.$domain";   Path = '/';             Expect = @(401) }
            [pscustomobject]@{ Name = "status.$domain";  Path = '/';             Expect = @(200, 302) }
            [pscustomobject]@{ Name = "logs.$domain";    Path = '/';             Expect = @(200, 302) }
            [pscustomobject]@{ Name = "metrics.$domain"; Path = '/';             Expect = @(200, 302) }
        )
    }

    if (-not $domain -or -not $ingressVip) {
        Add-Check -Step $step -Name 'DOMAIN and INGRESS_VIP from the live ConfigMap' -Status 'Fail' `
            -Detail "DOMAIN='$domain', INGRESS_VIP='$ingressVip' - without both, nothing below has an expectation to test DNS against"
    }
    else {
        Add-Check -Step $step -Name 'DOMAIN and INGRESS_VIP from the live ConfigMap' -Status 'Pass' -Detail "$domain -> $ingressVip, AUTH_MODE=$authMode"

        $vantage = Get-ClientVantagePoint -IngressVip $ingressVip
        $vantageSummary = @()
        if ($vantage.OnVipSubnet) { $vantageSummary += "on the VIP's own subnet ($($vantage.VipSubnetDetail))" }
        if ($vantage.TailnetAddress) { $vantageSummary += "on the tailnet ($($vantage.TailnetAddress))" }
        if ($vantageSummary.Count -eq 0) { $vantageSummary += 'neither on the VIP subnet nor on the tailnet - a routed client' }
        Write-Host "  This client: $($vantageSummary -join ', ')"

        # The hosts file, before anything is resolved. A stale line from a
        # Phase 5 spot-check would make every check below pass while proving
        # nothing about DNS, so it is a precondition of the stage rather than
        # one finding among several.
        $hostsPath = Get-HostsFilePath
        if (-not (Test-Path $hostsPath)) {
            Add-Check -Step $step -Name 'No hosts-file entry supplies any of the seven' -Status 'Pass' -Detail "$hostsPath does not exist"
        }
        else {
            $overrides = New-Object Collections.Generic.List[string]
            foreach ($line in (Get-Content $hostsPath)) {
                $stripped = ($line -split '#', 2)[0].Trim()
                if (-not $stripped) { continue }
                $fields = @($stripped -split '\s+')
                if ($fields.Count -lt 2) { continue }
                foreach ($field in $fields[1..($fields.Count - 1)]) {
                    if ($sites.Name -contains $field.ToLowerInvariant()) { $overrides.Add("$field -> $($fields[0])") }
                }
            }
            if ($overrides.Count -eq 0) {
                Add-Check -Step $step -Name 'No hosts-file entry supplies any of the seven' -Status 'Pass' -Detail "$hostsPath names none of them"
            }
            else {
                Add-Check -Step $step -Name 'No hosts-file entry supplies any of the seven' -Status 'Fail' -Detail "$hostsPath answers locally for $($overrides -join '; ') - every name check below is meaningless while it is there"
            }
        }

        $resolvers = @()
        if ($Resolver -and @($Resolver).Count -gt 0) {
            $resolvers = @($Resolver)
            Write-Host "  Querying the resolver(s) named on the command line: $($resolvers -join ', ')"
        }
        else {
            $resolvers = @(Get-ConfiguredResolver)
            if ($resolvers.Count -gt 0) { Write-Host "  Querying this client's configured resolver(s): $($resolvers -join ', ')" }
        }
        if ($resolvers.Count -eq 0) {
            Add-Check -Step $step -Name 'A resolver to query directly' -Status 'Fail' -Detail 'could not discover this client''s configured resolver - pass -Resolver <address> (the pfSense LAN address on the LAN)'
        }
        else {
            Add-Check -Step $step -Name 'A resolver to query directly' -Status 'Pass' -Detail ($resolvers -join ', ')
        }

        foreach ($site in $sites) {
            # (1) This client's own stack: cache and hosts file in the path.
            # "Contains the VIP" is deliberately not enough - a second A
            # record left pointing at the old Docker host round-robins, so
            # half of every client's connections land on a stopped machine.
            $client = Resolve-ViaClient -Name $site.Name
            if ($client.Error) {
                Add-Check -Step $step -Name "$($site.Name) resolves on this client" -Status 'Fail' -Detail $client.Error
            }
            elseif (@($client.Addresses).Count -eq 1 -and $client.Addresses[0] -eq $ingressVip) {
                Add-Check -Step $step -Name "$($site.Name) resolves on this client" -Status 'Pass' -Detail $ingressVip
            }
            else {
                Add-Check -Step $step -Name "$($site.Name) resolves on this client" -Status 'Fail' -Detail "answered $($client.Addresses -join ', '), expected exactly $ingressVip"
            }

            # (2) The resolver itself, over UDP/53 - the client's cache and
            # hosts file are out of this path, so a disagreement between
            # this and (1) localises the fault to the client.
            foreach ($resolverAddress in $resolvers) {
                $answer = Resolve-ViaServer -Name $site.Name -Server $resolverAddress
                if ($answer.Error) {
                    Add-Check -Step $step -Name "$($site.Name) at $resolverAddress" -Status 'Fail' -Detail $answer.Error
                }
                elseif (@($answer.Addresses).Count -eq 1 -and $answer.Addresses[0] -eq $ingressVip) {
                    Add-Check -Step $step -Name "$($site.Name) at $resolverAddress" -Status 'Pass' -Detail $ingressVip
                }
                else {
                    Add-Check -Step $step -Name "$($site.Name) at $resolverAddress" -Status 'Fail' -Detail "answered $($answer.Addresses -join ', '), expected exactly $ingressVip"
                }
            }
        }

        # ---------------------------------------------------------------- #
        Write-Stage 'Fetch'
        # ---------------------------------------------------------------- #

        foreach ($site in $sites) {
            $response = Invoke-HttpsHead -HostName $site.Name -Path $site.Path
            if (-not $response.Connected) {
                Add-Check -Step $step -Name "$($site.Name) answers from the VIP over TLS" -Status 'Fail' -Detail $response.Error
                continue
            }

            # The address, first: a 200 proves something answered, and only
            # this proves it was the cluster.
            if ($response.RemoteAddress -ne $ingressVip) {
                Add-Check -Step $step -Name "$($site.Name) answers from the VIP over TLS" -Status 'Fail' -Detail "connected to $($response.RemoteAddress), not $ingressVip - the name resolved somewhere else"
                continue
            }

            $reason = ''
            if (-not (Test-ProductionCertificate -Certificate $response.Certificate -Domain $domain -HostName $site.Name -Reason ([ref]$reason))) {
                Add-Check -Step $step -Name "$($site.Name) answers from the VIP over TLS" -Status 'Fail' -Detail "reached $ingressVip, but the certificate is wrong: $reason"
            }
            elseif ($site.Expect -contains $response.StatusCode) {
                Add-Check -Step $step -Name "$($site.Name) answers from the VIP over TLS" -Status 'Pass' -Detail "$($response.RemoteAddress), HTTP $($response.StatusCode), production certificate"
            }
            else {
                Add-Check -Step $step -Name "$($site.Name) answers from the VIP over TLS" -Status 'Fail' -Detail "reached $ingressVip with a valid certificate, but answered HTTP $($response.StatusCode) (expected $($site.Expect -join ' or '))"
            }
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Tree'
    # ---------------------------------------------------------------- #

    # A half-finished deletion is this phase's characteristic failure, and it
    # is a file check rather than a cluster one: the cluster is perfectly
    # happy while a compose file that describes a machine that no longer
    # exists sits in the tree waiting to be read as current.
    #
    # "The tree" is the *commit*, read with git ls-tree, and not the working
    # directory - which is a correctness fix rather than a preference. Found
    # on this repository's own Windows runner, 7c.11: its persistent
    # workspace carried a sparse-checkout index left behind by another
    # workflow, so deploy/ and charts/ were absent from disk after a checkout
    # whose log shows a clean fetch and a `sparse-checkout disable`. The
    # skip-worktree bits outlive the config that set them, and `git status`
    # calls that tree clean. A disk-based "is it gone?" check reads a
    # never-materialised directory as a deletion and passes, at full
    # confidence, on a repository that still holds every file it claims to
    # have removed. The commit cannot be fooled that way, and it is also the
    # thing the question is actually about: what a second operator gets when
    # they clone this.
    #
    # Presence on disk still counts *against* a must-not-exist path, since a
    # file sitting there is a finding whatever git thinks. The reverse does
    # not hold, which is why the must-exist list below is commit-only.
    $mustNotExist = @(
        @{ Path = 'compose.prod.yml'; Why = '7b.9' }
        @{ Path = 'compose.share.yml'; Why = '7b.9' }
        @{ Path = 'compose.observability.yml'; Why = '7b.9' }
        @{ Path = 'compose.metrics.yml'; Why = '7b.9' }
        @{ Path = 'compose.backup.yml'; Why = '7b.9' }
        @{ Path = '.github/workflows/cd.yml'; Why = 'deleted whole, mid-soak' }
        @{ Path = '.github/workflows/cutover-tag-snapshot.yml'; Why = '7b.9, with the compose files' }
        @{ Path = 'containers/caddy'; Why = '7b.9 - Traefik replaced it' }
        @{ Path = 'containers/fluent-bit'; Why = '7b.9 - deploy/ holds the cluster copies' }
        @{ Path = 'containers/prometheus'; Why = '7b.9' }
        @{ Path = 'containers/grafana'; Why = '7b.9' }
        @{ Path = 'containers/opensearch-provision'; Why = '7b.9' }
        @{ Path = 'containers/autokuma'; Why = '7b.9' }
    )
    $leftBehind = @($mustNotExist |
            Where-Object {
                $candidate = $_.Path
                $inCommit = @($treeFiles | Where-Object { $_ -eq $candidate -or $_.StartsWith("$candidate/") } | Select-Object -First 1).Count -gt 0
                $onDisk = Test-Path (Join-Path $repositoryRootPath $candidate)
                $inCommit -or $onDisk
            } |
            ForEach-Object { "$($_.Path) ($($_.Why))" })
    if ($leftBehind.Count -gt 0) {
        Add-Check -Step $step -Name 'The compose path is gone from the tree (7b.9)' -Status 'Fail' -Detail ($leftBehind -join '; ')
    }
    else {
        Add-Check -Step $step -Name 'The compose path is gone from the tree (7b.9)' -Status 'Pass' -Detail "$($mustNotExist.Count) path(s) checked, none present"
    }

    # The mirror failure, and the reason this is not a `git rm -r containers/`:
    # four of these survive on purpose, and a gate that only ever checks for
    # absence cannot tell a finished cutover from an over-enthusiastic one.
    $mustExist = @(
        @{ Path = 'compose.yaml'; Why = 'local dev, untouched by this phase' }
        @{ Path = 'containers/aerie-db'; Why = 'compose.yaml builds it for local dev' }
        @{ Path = 'containers/backup'; Why = 'restore-job.yaml and Phase 8 run it' }
        @{ Path = 'containers/kuma-provision'; Why = '6b.13 made it a cluster image' }
    )
    $overDeleted = @($mustExist |
            Where-Object {
                $candidate = $_.Path
                -not (@($treeFiles | Where-Object { $_ -eq $candidate -or $_.StartsWith("$candidate/") } | Select-Object -First 1).Count -gt 0)
            } |
            ForEach-Object { "$($_.Path) - $($_.Why)" })
    if ($overDeleted.Count -gt 0) {
        Add-Check -Step $step -Name 'The local-dev path survives (7b.9)' -Status 'Fail' -Detail ($overDeleted -join '; ')
    }
    else {
        Add-Check -Step $step -Name 'The local-dev path survives (7b.9)' -Status 'Pass' -Detail "$($mustExist.Count) path(s) checked, all present"
    }

    # publish.yml keeps its other jobs, so its deletion is a grep rather than
    # a file check - the one piece of 7b.9 that edits a file instead of
    # removing one.
    $publishInCommit = @($treeFiles | Where-Object { $_ -eq '.github/workflows/publish.yml' }).Count -gt 0
    if (-not $publishInCommit) {
        Add-Check -Step $step -Name 'No aerie-caddy job in publish.yml (7b.9)' -Status 'Fail' -Detail '.github/workflows/publish.yml is not in HEAD - it is not on this phase''s deletion list'
    }
    else {
        # -I so a binary file can never be matched, --fixed-strings so
        # nothing here is a regex. Exit code 1 from `git grep` is "no match",
        # which is the passing answer; anything above 1 is an error and gets
        # reported as one rather than read as a pass.
        $caddyGrep = Invoke-Git -RepositoryRoot $repositoryRootPath -Arguments @('grep', '-n', '-I', '--fixed-strings', '-i', 'caddy', 'HEAD', '--', '.github/workflows/publish.yml')
        if ($caddyGrep.ExitCode -eq 1) {
            Add-Check -Step $step -Name 'No aerie-caddy job in publish.yml (7b.9)' -Status 'Pass' -Detail 'publish.yml does not name caddy'
        }
        elseif ($caddyGrep.ExitCode -eq 0) {
            $lines = @($caddyGrep.StdOut -split "`r?`n" | Where-Object { $_ } | Select-Object -First 5)
            Add-Check -Step $step -Name 'No aerie-caddy job in publish.yml (7b.9)' -Status 'Fail' -Detail "publish.yml still names caddy: $($lines -join '; ')"
        }
        else {
            Add-Check -Step $step -Name 'No aerie-caddy job in publish.yml (7b.9)' -Status 'Fail' -Detail "git grep failed (exit $($caddyGrep.ExitCode)): $((($caddyGrep.StdErr) -replace '\s+', ' ').Trim())"
        }
    }

    # The portability check (design.md, docs/ethos.md) with one specific
    # value, since this is the phase that retires it. Note what is *not*
    # wrong: after 7c.4 the rebuilt host usually keeps this address, so it
    # legitimately appears in the ConfigMap under WINDOWS_EXPORTER_TARGETS
    # and SHARE_HOST. What must not exist is a literal in the tree - a
    # comment quoting it counts, because the next operator reads it and it is
    # still wrong for them.
    if (-not $LegacyHostAddress) {
        Add-Check -Step $step -Name 'The old host''s address appears nowhere in the tree' -Status 'Fail' `
            -Detail 'no -LegacyHostAddress and no $env:LEGACY_HOST_ADDRESS - a check this gate cannot evaluate is a failure rather than a skip'
    }
    elseif ($LegacyHostAddress -notmatch '^(\d{1,3}\.){3}\d{1,3}$') {
        Add-Check -Step $step -Name 'The old host''s address appears nowhere in the tree' -Status 'Fail' -Detail "-LegacyHostAddress '$LegacyHostAddress' is not a bare IPv4 address"
    }
    else {
        $scanRoots = @('deploy', 'charts', 'scripts', '.github')
        $scannedCount = @($treeFiles | Where-Object { $path = $_; @($scanRoots | Where-Object { $path.StartsWith("$_/") }).Count -gt 0 }).Count
        $addressGrep = Invoke-Git -RepositoryRoot $repositoryRootPath -Arguments (@('grep', '-n', '-I', '--fixed-strings', $LegacyHostAddress, 'HEAD', '--') + $scanRoots)
        if ($addressGrep.ExitCode -eq 1) {
            Add-Check -Step $step -Name 'The old host''s address appears nowhere in the tree' -Status 'Pass' -Detail "$scannedCount file(s) under $($scanRoots -join '/, ')/ name nothing at $LegacyHostAddress"
        }
        elseif ($addressGrep.ExitCode -eq 0) {
            # `git grep <commit>` prefixes every hit with the commit, so a
            # line reads HEAD:<path>:<line>:<text>. Only the first three
            # fields are wanted - the text is whatever quoted the address,
            # and printing it here would put the value in the table twice.
            $where = @($addressGrep.StdOut -split "`r?`n" |
                    Where-Object { $_ -match '^[^:]+:(?<path>[^:]+):(?<line>\d+):' } |
                    ForEach-Object { "$($Matches['path']):$($Matches['line'])" } |
                    Select-Object -First 8)
            Add-Check -Step $step -Name 'The old host''s address appears nowhere in the tree' -Status 'Fail' `
                -Detail "$LegacyHostAddress at $($where -join '; ') - it belongs in the tree as a `${...} substitution and nowhere else (docs/ethos.md)"
        }
        else {
            Add-Check -Step $step -Name 'The old host''s address appears nowhere in the tree' -Status 'Fail' -Detail "git grep failed (exit $($addressGrep.ExitCode)): $((($addressGrep.StdErr) -replace '\s+', ' ').Trim())"
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
    Select-Object Step, Check, Result, @{ Name = 'Detail'; Expression = { if ($_.Detail.Length -gt 90) { $_.Detail.Substring(0, 89) + '...' } else { $_.Detail } } } |
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
        "## Phase 7 cutover gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { 'pass' } 'Warn' { 'warn' } default { '**FAIL**' } }
        $detail = ($check.Detail -replace '\|', '\|')
        $lines += "| $($check.Step) | $($check.Check) | $mark | $detail |"
    }
    $lines += @(
        ''
        "_The cluster plan, Phase 7c.11. Read-only: ${tick}kubectl get${tick}, one proxied GET to Prometheus, DNS queries and TLS handshakes - all of them by name, with no ${tick}--resolve${tick} anywhere. A self-hosted runner is a LAN client, so the name checks above are the LAN vantage point; ${tick}scripts/k3s/Test-NameResolution.ps1${tick} run by hand from a tailnet client is the other one._"
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 7 cutover gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names its check; docs/plans/swarm/phase-7-cutover.md 7c.11 has the reasoning for each.'
    exit 1
}

Write-Host "Phase 7 cutover gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'This run resolved every name normally, from wherever it was run. That is the LAN half'
Write-Host 'of the name checks; run scripts/k3s/Test-NameResolution.ps1 from a tailnet client for the other.'
exit 0
