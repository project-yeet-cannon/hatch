<#
.SYNOPSIS
    Asserts the cluster plan Phase 7b.6: the seven hostnames resolve to
    ${INGRESS_VIP} through the house's own resolver, and answer from it -
    with no --resolve, no /etc/hosts, and no answer supplied by this script.

.DESCRIPTION
    The first time in the entire plan this check is possible. Every gate
    before it - Test-ClusterPlatform.ps1 (3b.13), Test-DataTier.ps1 (4b.11),
    Test-AppTier.ps1 (5b.14), Test-Observability.ps1 (6b.15) - reaches the
    cluster by dialling the VIP directly and sending the hostname as SNI,
    which is curl's --resolve by another name, because until 7b.4 pointed
    pfSense Unbound at the VIP no DNS record existed to follow. This script
    is the one that must not do that: the property under test is that the
    resolver sends the house to the cluster, and a check that supplies the
    answer cannot see it.

    That single property is why this is its own script rather than four more
    assertions in Test-AppTier.ps1, and why it deliberately breaks the rule
    every gate above states:

    **Expectations come from parameters here, not from the cluster.** The
    other scripts read DOMAIN and INGRESS_VIP out of the live
    aerie-cluster-config ConfigMap over SSH. This one has to run from
    whatever client is in front of you - a laptop on the LAN, a phone's
    tethered laptop on the tailnet - with no kubeconfig, no node SSH key and
    no cluster credentials of any kind, because "what does a client see" is
    the question. So -Domain and -IngressVip are supplied, and fall back to
    $env:DOMAIN / $env:INGRESS_VIP, which is what the workflow wrapper sets
    them from (vars.DOMAIN, vars.INGRESS_VIP - the same two repository
    variables provision-4-cluster-config.yml renders the ConfigMap from, so
    the expectation still has exactly one source).

    Three independent things are checked per hostname, and they fail for
    different reasons on purpose:

    1. **The client's own resolver** (System.Net.Dns, so the OS cache and the
       hosts file are both in the path) returns exactly [INGRESS_VIP] - the
       full answer set, not "contains". A leftover A record for the old
       Docker host alongside the new one round-robins: half of every client's
       connections land on a machine that 7b.2 stopped, and a check that
       accepted "the VIP is in the set" would pass for a house that is broken
       half the time.
    2. **The resolver itself**, queried directly over UDP/53 with the
       recursion-desired bit set - the `dig @<pfsense-lan-ip>` half of 7b.6.
       This bypasses the client's cache and its hosts file entirely, so a
       disagreement between this and (1) localises the fault to the client
       rather than to pfSense. The query is built and parsed here rather than
       shelled out to `dig` or `Resolve-DnsName`: `dig` is absent on Windows,
       `Resolve-DnsName` is absent everywhere else, and this script has to run
       on both a Windows Server runner and whatever laptop is on the tailnet.
    3. **The fetch, by name.** One TLS connection per hostname, opened against
       the *hostname* - the OS resolves it again, exactly as a browser would -
       and then the socket's own remote endpoint is read back and asserted
       against INGRESS_VIP. That read is curl's `%{remote_ip}` and it is the
       point of the whole step: a 200 proves something answered, and only the
       address proves it was the cluster.

    Plus one negative check that keeps (1) and (3) honest: **no hosts-file
    entry names any of the seven**. Without it, a stale line left over from a
    Phase 5 spot-check would make every other check in this script pass while
    proving nothing about DNS at all.

    Stages:
      1. Preflight  - the two expectations parse, the hosts file names none
                      of the seven, and this client says where it is standing.
      2. Resolvers  - which resolver addresses stage 3 will ask directly.
      3. Resolution - per name: this client's own answer, then each
                      resolver's, both asserted as an exact set.
      4. Fetch      - per name: one TLS connection opened by name, and the
                      address it actually reached.
      5. Report     - one table, one exit code.

    Same three properties every phase script here states and relies on:
    it does not stop at the first failure; a check it cannot evaluate is a
    failure rather than a skip; and it writes nothing, so it is safe to run
    at any time, as many times as you like.

    **One run cannot satisfy 7b.6 on its own.** The exit condition is "from
    the LAN *and* from the tailnet", and no client is both at once from DNS's
    point of view - a tailnet client's resolver is Tailscale's 100.100.100.100
    forwarding ${DOMAIN} on to this same pfSense by split DNS. So the script
    reports which vantage point it just ran from, and says so again at the
    end. Run it twice, from two places.

    ASCII only, in code and in comments - see cutover-tag-snapshot.yml's own
    note for the full account. Windows PowerShell 5.1 decodes a BOM-less
    script as the ANSI code page rather than as UTF-8, and an em dash's third
    byte is a curly quote there, which 5.1 treats as a real string delimiter.
    This file is run by hand from unpredictable places; it stays in the
    subset that cannot be mis-decoded.

.PARAMETER Domain
    The base domain the cluster serves - just the domain, no scheme and no
    leading dot. Defaults to $env:DOMAIN.

.PARAMETER IngressVip
    The floating ingress address every hostname must resolve to and answer
    from. Defaults to $env:INGRESS_VIP.

.PARAMETER Resolver
    One or more resolver addresses to query directly for check (2). Defaults
    to whatever this client is configured to use, discovered per-platform -
    which on the LAN is pfSense and on the tailnet is Tailscale's stub
    resolver, and both are worth asserting against.

.PARAMETER TimeoutSeconds
    Per-connection budget for both the DNS queries and the TLS fetches.

.EXAMPLE
    .\Test-NameResolution.ps1 -Domain example.com -IngressVip 10.0.0.30

.EXAMPLE
    .\Test-NameResolution.ps1 -Domain example.com -IngressVip 10.0.0.30 -Resolver 10.0.0.1
#>
[CmdletBinding()]
param(
    [string]$Domain = $env:DOMAIN,

    [string]$IngressVip = $env:INGRESS_VIP,

    [string[]]$Resolver,

    [ValidateRange(2, 60)]
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Checks = New-Object Collections.Generic.List[object]
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

function Get-ExceptionMessage {
    <#
    .SYNOPSIS
        The innermost exception's message, without PowerShell's
        'Exception calling "X" with "1" argument(s)' wrapper around it.

    .DESCRIPTION
        Every network failure this script reports arrives as a
        MethodInvocationException wrapping the SocketException that actually
        says what happened. The wrapper is 60 characters of noise in front of
        "no such host is known", and this table has to be readable at a
        glance during a cutover.
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
        strings, discovered without assuming a platform.

    .DESCRIPTION
        Windows first, through Get-DnsClientServerAddress, filtered to
        interfaces that are actually up: a laptop carries DNS settings on
        every adapter it has ever seen, and querying a disconnected VPN
        adapter's resolver produces a timeout that reads as a pfSense
        failure.

        Everywhere else, /etc/resolv.conf, which both macOS and Linux keep
        pointed at the current primary resolver. On a tailnet client with
        MagicDNS this is Tailscale's 100.100.100.100 stub, and that is the
        correct thing to query from there - forwarding ${DOMAIN} on to
        pfSense is precisely the split-DNS behaviour 7b.6 wants proven.

        Loopback stubs other than Tailscale's are dropped: systemd-resolved's
        127.0.0.53 answers, but answers out of its own cache with no way to
        ask what it forwarded to, which is check (1)'s job rather than
        check (2)'s.
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
            # Both cmdlets ship with Windows 8/2012 and later; a client old
            # enough to lack them is a -Resolver away from being usable.
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
        the tailnet, or neither - reported rather than asserted.

    .DESCRIPTION
        The subnet test uses each interface's own prefix mask rather than
        assuming a /24, because guessing the mask would mislabel exactly the
        run whose result matters most. The tailnet test is CGNAT space,
        100.64.0.0/10, which is what Tailscale assigns.
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

            # IPv4Mask is $null on some platforms for some adapter types; a
            # missing mask means "cannot tell", which is not "not on it".
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

    # The leading comma keeps this a byte[]. Without it PowerShell unrolls the
    # array on the way out of the function and the caller holds an Object[],
    # which UdpClient.Send(byte[], int) then has to be coerced into - it works
    # today, and it is one overload-resolution change away from not working,
    # for no benefit. The comma costs nothing and removes the question.
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

    .DESCRIPTION
        Returns .Addresses (possibly empty), .Error ($null on success) and
        .Rcode. A CNAME chain the recursor followed lands in the same answer
        section, so collecting every type-A answer handles it without this
        function having to understand CNAMEs.

        UDP only, and truncation is reported rather than retried over TCP:
        seven names with one address each never approach 512 bytes, so a TC
        bit here means something is answering that this script should not
        quietly paper over.
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
        # [int] casts on every shifted byte, everywhere below: PowerShell's
        # -shl returns the *left operand's* type, so [byte]0x36 -shl 8 is 0
        # rather than 13824 and every two-byte field silently collapses to its
        # low half. It is a wrong answer rather than an error, which is why it
        # is called out here instead of being left to read as obvious.
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
        Windows PowerShell 5.1 has no GetNameInfo overload for SANs and
        X509SubjectAlternativeNameExtension is .NET 7 and later, so the
        extension is formatted and parsed - and *this* script is the one that
        has to parse both spellings, because it is the only one in
        scripts/k3s/ that runs somewhere other than a Windows host.
        X509Extension.Format is implemented by the platform's own crypto
        stack: Windows renders `DNS Name=host`, while OpenSSL (so macOS and
        Linux, so the tailnet laptop half of 7b.6) renders `DNS:host`. A
        parser that knows only the Windows spelling finds no names at all on
        a Mac and reports every certificate as not covering its hostname -
        a confident, entirely wrong failure.

        The second pass is for a localised Windows, where the label itself is
        translated but the `<label><separator><value>` shape is not: it takes
        whatever follows the last separator in each comma-delimited part. It
        only runs when the first pass found nothing, so a normal certificate
        never reaches it.
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
        it is not appended to $Reason when it fails. Same shape as
        Test-Observability.ps1's, which is where it was lifted from.
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

        Only the status line and the headers are read; the body is never
        touched, so chunked encoding never has to be understood here. That is
        also why the request is cheap enough to run against all seven names
        in a couple of seconds.

        Certificate validation is accepted unconditionally - the certificate
        is the thing under inspection, not a precondition of the request
        completing, the same choice Test-AppTier.ps1's Invoke-HttpsGet makes.
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
        # check would look rigorous and be permanently red. A peer that is
        # genuinely IPv6 is left alone and fails the comparison on its merits,
        # which is correct: the VIP is an IPv4 address.
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
        $writer.WriteLine('User-Agent: aerie-test-name-resolution')
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

$startedUtc = (Get-Date).ToUniversalTime()

# ---------------------------------------------------------------- #
Write-Stage 'Preflight'
# ---------------------------------------------------------------- #

# Neither value is [Parameter(Mandatory)], because both fall back to the
# environment for the workflow wrapper's sake - so both are validated here
# instead, against the same patterns scripts/k3s/cluster-config.json declares
# for DOMAIN and INGRESS_VIP. A missing one is a usage error rather than a
# failed check: there is nothing to test without them.
if (-not $Domain) { throw 'No -Domain and no $env:DOMAIN. Pass the base domain the cluster serves, e.g. -Domain example.com.' }
if (-not $IngressVip) { throw 'No -IngressVip and no $env:INGRESS_VIP. Pass the floating ingress address, e.g. -IngressVip 10.0.0.30.' }
$Domain = $Domain.Trim().TrimEnd('.').ToLowerInvariant()
$IngressVip = $IngressVip.Trim()
if ($Domain -notmatch '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$') {
    throw "-Domain '$Domain' is not a bare domain. No scheme, no trailing dot, no wildcard - the wildcard is added where it is needed."
}
if ($IngressVip -notmatch '^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])$') {
    throw "-IngressVip '$IngressVip' is not a bare IPv4 address."
}

Write-Host "  Domain      : $Domain"
Write-Host "  Ingress VIP : $IngressVip"

# The seven, in the order 7b.6's own loop names them, each with the path that
# gives a deterministic answer and the codes that count as "the cluster
# answered". Where more than one code is listed the reason is in the note:
# these are session-less GETs, and three of the seven are applications that
# bounce an anonymous caller to their own login page. The status code is the
# weaker half of every row anyway - the address is what is being proven, and
# the code is only here so that a 502 from a healthy-looking Traefik is not
# read as a pass.
$sites = @(
    [pscustomobject]@{ Name = "home.$Domain";    Path = '/health/ready'; Expect = @(200);      Note = 'the API readiness probe' }
    [pscustomobject]@{ Name = "kiosk.$Domain";   Path = '/';             Expect = @(200);      Note = 'the dashboard SPA, root-rewritten by middleware-kiosk.yaml' }
    [pscustomobject]@{ Name = "files.$Domain";   Path = '/version.json'; Expect = @(200);      Note = 'the published app manifest' }
    [pscustomobject]@{ Name = "share.$Domain";   Path = '/';             Expect = @(401);      Note = 'dufs gates the path, not just writes - a 200 here would be the finding' }
    [pscustomobject]@{ Name = "status.$Domain";  Path = '/';             Expect = @(200, 302); Note = 'Uptime Kuma serves its SPA, or redirects to it' }
    [pscustomobject]@{ Name = "logs.$Domain";    Path = '/';             Expect = @(200, 302); Note = 'OpenSearch Dashboards redirects an anonymous caller to its login' }
    [pscustomobject]@{ Name = "metrics.$Domain"; Path = '/';             Expect = @(200, 302); Note = 'Grafana redirects an anonymous caller to /login' }
)

$vantage = Get-ClientVantagePoint -IngressVip $IngressVip
$vantageSummary = @()
if ($vantage.OnVipSubnet) { $vantageSummary += "on the VIP's own subnet ($($vantage.VipSubnetDetail))" }
if ($vantage.TailnetAddress) { $vantageSummary += "on the tailnet ($($vantage.TailnetAddress))" }
if ($vantageSummary.Count -eq 0) { $vantageSummary += 'neither on the VIP subnet nor on the tailnet - a routed client' }
Write-Host "  This client : $($vantageSummary -join ', ')"

# The hosts file, before anything is resolved. A stale line from a Phase 5
# spot-check would make every check below pass while proving nothing, so this
# is a precondition of the run rather than one finding among several.
$hostsPath = Get-HostsFilePath
if (-not (Test-Path $hostsPath)) {
    Add-Check -Step '7b.6' -Name 'No hosts-file entry supplies any of the seven' -Status 'Pass' -Detail "$hostsPath does not exist"
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
        Add-Check -Step '7b.6' -Name 'No hosts-file entry supplies any of the seven' -Status 'Pass' -Detail "$hostsPath names none of them"
    }
    else {
        Add-Check -Step '7b.6' -Name 'No hosts-file entry supplies any of the seven' -Status 'Fail' -Detail "$hostsPath answers locally for $($overrides -join '; ') - remove the line and re-run; every check below is meaningless while it is there"
    }
}

# ---------------------------------------------------------------- #
Write-Stage 'Resolvers'
# ---------------------------------------------------------------- #

$resolvers = @()
if ($Resolver -and @($Resolver).Count -gt 0) {
    $resolvers = @($Resolver)
    Write-Host "  Querying the resolver(s) named on the command line: $($resolvers -join ', ')"
}
else {
    $resolvers = @(Get-ConfiguredResolver)
    if ($resolvers.Count -gt 0) {
        Write-Host "  Querying this client's configured resolver(s): $($resolvers -join ', ')"
    }
}

if ($resolvers.Count -eq 0) {
    # Unproven is a failure, not a skip - the same rule every gate in this
    # phase's lineage states. Half of 7b.6 is "ask the resolver itself", and
    # a run that could not find one has not asked.
    Add-Check -Step '7b.6' -Name 'A resolver to query directly' -Status 'Fail' -Detail 'could not discover this client''s configured resolver - pass -Resolver <address> (the pfSense LAN address on the LAN, 100.100.100.100 on a tailnet client)'
}
else {
    Add-Check -Step '7b.6' -Name 'A resolver to query directly' -Status 'Pass' -Detail ($resolvers -join ', ')
}

# ---------------------------------------------------------------- #
Write-Stage 'Resolution'
# ---------------------------------------------------------------- #

foreach ($site in $sites) {
    # (1) This client's own stack: cache and hosts file in the path.
    $client = Resolve-ViaClient -Name $site.Name
    if ($client.Error) {
        Add-Check -Step '7b.6' -Name "$($site.Name) resolves on this client" -Status 'Fail' -Detail $client.Error
    }
    elseif (@($client.Addresses).Count -eq 1 -and $client.Addresses[0] -eq $IngressVip) {
        Add-Check -Step '7b.6' -Name "$($site.Name) resolves on this client" -Status 'Pass' -Detail $IngressVip
    }
    else {
        # "Contains the VIP" is deliberately not enough: a second A record
        # left pointing at the old Docker host round-robins, so half of every
        # client's connections land on a machine 7b.2 stopped.
        Add-Check -Step '7b.6' -Name "$($site.Name) resolves on this client" -Status 'Fail' -Detail "answered $($client.Addresses -join ', '), expected exactly $IngressVip"
    }

    # (2) The resolver itself, over UDP/53, cache and hosts file bypassed.
    foreach ($server in $resolvers) {
        $direct = Resolve-ViaServer -Name $site.Name -Server $server
        if ($direct.Error) {
            Add-Check -Step '7b.6' -Name "$($site.Name) resolves at $server" -Status 'Fail' -Detail $direct.Error
        }
        elseif (@($direct.Addresses).Count -eq 1 -and $direct.Addresses[0] -eq $IngressVip) {
            Add-Check -Step '7b.6' -Name "$($site.Name) resolves at $server" -Status 'Pass' -Detail $IngressVip
        }
        else {
            Add-Check -Step '7b.6' -Name "$($site.Name) resolves at $server" -Status 'Fail' -Detail "answered $($direct.Addresses -join ', '), expected exactly $IngressVip"
        }
    }
}

# ---------------------------------------------------------------- #
Write-Stage 'Fetch'
# ---------------------------------------------------------------- #

# By name, with no address supplied anywhere - and then the address the
# socket actually reached, read back off the connection. This is the half of
# 7b.6 that no earlier gate could run.
foreach ($site in $sites) {
    $response = Invoke-HttpsHead -HostName $site.Name -Path $site.Path
    $label = "$($site.Name) answers from the VIP"

    if (-not $response.Connected) {
        $reached = if ($response.RemoteAddress) { " (connected to $($response.RemoteAddress) first)" } else { '' }
        Add-Check -Step '7b.6' -Name $label -Status 'Fail' -Detail "$($response.Error)$reached"
        continue
    }

    if ($response.RemoteAddress -ne $IngressVip) {
        Add-Check -Step '7b.6' -Name $label -Status 'Fail' -Detail "answered from $($response.RemoteAddress), not $IngressVip - something other than the cluster is serving this name"
        continue
    }

    $certReason = $null
    $certOk = Test-ProductionCertificate -Certificate $response.Certificate -Domain $Domain -HostName $site.Name -Reason ([ref]$certReason)
    if (-not $certOk) {
        Add-Check -Step '7b.6' -Name $label -Status 'Fail' -Detail "reached $IngressVip, but the certificate is wrong: $certReason"
        continue
    }

    if ($site.Expect -contains $response.StatusCode) {
        Add-Check -Step '7b.6' -Name $label -Status 'Pass' -Detail "$IngressVip, $($response.StatusCode) on $($site.Path), production certificate"
    }
    else {
        Add-Check -Step '7b.6' -Name $label -Status 'Fail' -Detail "$IngressVip with a good certificate, but $($site.Path) returned $($response.StatusCode), expected $($site.Expect -join ' or ') - $($site.Note)"
    }
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
        "## Phase 7b.6 name resolution - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, in ${elapsed} min."
        ''
        "Seen from: $($vantageSummary -join ', '). Resolver(s) queried directly: $(if ($resolvers.Count -gt 0) { $resolvers -join ', ' } else { 'none' })."
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
        "_The cluster plan, Phase 7b.6. Read-only: DNS queries and TLS handshakes, all of them by name. A self-hosted runner is a LAN client, so this run is the LAN half of the step; run ${tick}scripts/k3s/Test-NameResolution.ps1${tick} by hand from a tailnet client for the other half._"
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Phase 7b.6 name resolution FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. Every failure above names its check; docs/plans/swarm/phase-7-cutover.md 7b.6 has the reasoning.'
    exit 1
}

Write-Host "Phase 7b.6 name resolution passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
Write-Host ''
if ($vantage.TailnetAddress -and -not $vantage.OnVipSubnet) {
    Write-Host 'That was the tailnet half. Run it again from a client on the LAN to finish 7b.6.'
}
elseif ($vantage.OnVipSubnet -and -not $vantage.TailnetAddress) {
    Write-Host 'That was the LAN half. Run it again from a tailnet client to finish 7b.6 -'
    Write-Host 'split DNS forwards the domain to this same resolver, and confirming that is cheaper than assuming it.'
}
else {
    Write-Host 'Run this from both vantage points before ticking 7b.6: a client on the LAN, and a client on the tailnet.'
}
exit 0
