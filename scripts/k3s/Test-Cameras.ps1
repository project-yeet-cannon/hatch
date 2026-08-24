<#
.SYNOPSIS
    Asserts the cluster half of the cameras plan's first-camera bring-up
    (docs/camera-devices-architecture.md), in the shape scripts/k3s/Test-DataTier.ps1
    established for a phase gate: read-only, one round trip, one table.

.DESCRIPTION
    Camera bring-up splits cleanly in two. One half needs a person - walking in
    front of the camera, watching a modal open on a wall tablet, judging whether
    the feed feels live; those items are listed under "Still needs a person"
    below, and in the architecture doc. The other half is a set of yes/no facts
    about a cluster: is go2rtc running in the shape that lets Aerie register a
    stream, and - the item that could force a design change - can a *pod* reach
    a camera's RTSP port at all.

    This is that other half, as a command. It exists because those facts are
    re-asked every time a camera is added, not once at bring-up, and because the
    pod-to-camera hop is the one link no amount of local testing reaches.

    Three properties, the same three Test-DataTier.ps1 states:

    **It does not stop at the first failure.** Nothing here writes anything.

    **A check it cannot evaluate is a failure, not a skip.** An absent object
    or an unreadable response is "not proven", and a gate that reports that as
    a skip can be satisfied by a cluster that is switched off.

    **It takes no camera name.** Which streams to prove comes from go2rtc
    itself, and which cameras exist comes from Aerie's database by way of what
    has been registered. There is nothing here to keep up to date as cameras
    are added.

    **No credential leaves the node.** That constraint shapes the probe more
    than anything else. A go2rtc stream source is an RTSP URL with the camera's
    password in it, `GET /api/streams` returns those URLs verbatim, and this
    script's output is a CI run log. So the probe never renders that body: it
    pipes it through a python one-liner on the node that prints the top-level
    keys and nothing else, so what crosses the wire is stream *names*.
    Everything else is a byte count.

    Stages:
      1. Preflight - the SSH key resolves, the client is present, and the
                     node answers 22.
      2. Probe     - one round trip: the Deployment, the pods, whether the
                     Service answers, which streams go2rtc currently holds,
                     and one JPEG frame for each of them through the API
                     server's service proxy.
      3. Checks    - evaluated against that snapshot.
      4. Report    - one table, one exit code.

    What a green run does and does not prove. With streams registered, it
    proves the camera path up to the browser: go2rtc is running in the shape
    that lets Aerie register a stream, the pod network reaches the camera's
    RTSP port, the credential Aerie sent was accepted, and the camera is
    producing decodable video. With none registered - which is the normal state
    of an idle go2rtc, since registration is lazy - it proves everything except
    that last hop, and says so rather than passing quietly. It says nothing
    about HA's motion events, the SSE hop, or whether MediaSource works in the
    kiosk's WebView; the report repeats which ones those are.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here is cluster
    state.

.PARAMETER MinimumFrameBytes
    How large a response has to be to count as a frame. go2rtc answers a
    working stream with a JPEG of the sub-stream's own resolution - tens of
    kilobytes at 640x480 - and answers a broken one with either a non-2xx
    (which `kubectl get --raw` turns into a non-zero exit, reported as 0 bytes
    here) or a short error body. The default sits far above the second and far
    below the first, so it does not have to distinguish them.

.EXAMPLE
    .\Test-Cameras.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    # The namespace charts/aerie installs into. A parameter only so a second
    # installation on one cluster can be checked; not something to tune.
    [string]$Namespace = 'aerie',

    [int]$MinimumFrameBytes = 2000,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# The Deployment, Service and port charts/aerie names for go2rtc. Constants
# rather than parameters: they are this chart's own, and a gate that takes the
# name of the thing it is proving as an argument can be pointed at anything.
$Go2RtcDeployment = 'go2rtc'
$Go2RtcService = 'go2rtc'
$Go2RtcPort = 1984
$Go2RtcComponentLabel = 'app.kubernetes.io/component=go2rtc'

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
        link is missing. See Test-DataTier.ps1's copy for why an absent path
        and an absent value both have to mean "not proven" in a gate.
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

function Format-Condition {
    param([Parameter(Mandatory)][AllowNull()]$Condition, [int]$MaxLength = 160)
    if ($null -eq $Condition) { return 'no condition reported' }
    $reason = [string](Get-Field $Condition 'reason')
    $message = [string](Get-Field $Condition 'message') -replace '\s+', ' '
    $text = (@($reason, $message) | Where-Object { $_ }) -join ': '
    if (-not $text) { $text = [string](Get-Field $Condition 'status') }
    if ($text.Length -gt $MaxLength) { $text = $text.Substring(0, $MaxLength - 1) + '...' }
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
    Write-Host ("  [{0}] {1,-8} {2}{3}" -f $marker, $Step, $Name, $(if ($Detail) { " - $Detail" })) -ForegroundColor $colour
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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-cameras-gate'
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

    Write-Host "Cluster:   $IPAddress"
    Write-Host "Repo:      $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    Write-Host "Namespace: $Namespace"
    if ($keyFingerprint) { Write-Host "SSH key:  $keyFingerprint" }
    Write-Host 'Preflight OK. Everything below is read-only, and no stream source - which is to say no camera password - is printed.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-cameras-gate-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    # One round trip. Every command falls back to `echo {}` or a zero so a
    # half-built camera path answers "not there" rather than aborting the
    # probe on its first missing object. Single quotes only, no double quotes
    # anywhere in $probeScript - Invoke-NodeSsh refuses a command containing
    # one, because Windows PowerShell 5.1 lets ssh.exe strip it and run a
    # subtly different script on the node.
    #
    # `set -f` first, and it is load-bearing rather than tidy: the frame URLs
    # below carry a `?src=` query string, and `?` is a glob character. It
    # would not match anything today - /api/v1/... is not a path on the node -
    # but the alternative to disabling globbing is quoting, and the quoting
    # character that would survive both shells is the one this script is not
    # allowed to use.
    #
    # Stream names are read out of go2rtc, but never its response body: that
    # body carries every camera's password, and this output is a CI log. The
    # parse happens inside a pipeline on the node (see below) and only top-level
    # keys - the stream names - are ever printed.
    $kubectl = "sudo k3s kubectl -n $Namespace"
    $rawBase = "/api/v1/namespaces/$Namespace/services/${Go2RtcService}:$Go2RtcPort/proxy"

    # Stream *names* only. python3 loads go2rtc's /api/streams and prints
    # nothing but its top-level keys, so no producer URL is ever rendered -
    # that body carries each camera's password, and this output is a CI log.
    #
    # What this answers is "what has go2rtc been told about", which since
    # registration became lazy is a question about what has been *watched*
    # recently rather than what is configured: Aerie registers a stream
    # immediately before it relays one, so an idle go2rtc legitimately holds
    # none.
    $registeredLine = "$kubectl get --raw $rawBase/api/streams --request-timeout=10s 2>/dev/null" +
        " | python3 -c 'import json,sys" + [char]0x0A + "for k in json.load(sys.stdin): print(k)' 2>/dev/null || true"

    # The frame loop, built by concatenation rather than interpolation: `$(`
    # and `$S` are the *shell's* dollars, and a double-quoted PowerShell
    # string would consume them before ssh ever saw them.
    $framesLine = 'for S in $(' + $registeredLine + '); do printf ''%s '' $S; ' +
        $kubectl + ' get --raw ' + $rawBase + '/api/frame.jpeg?src=$S --request-timeout=25s 2>/dev/null | wc -c; done'

    $probeScript = @(
        'set -f'
        "printf '\n--- deployment\n'"
        "$kubectl get deployment $Go2RtcDeployment -o json 2>/dev/null || echo {}"
        "printf '\n--- pods\n'"
        "$kubectl get pods -l $Go2RtcComponentLabel -o json 2>/dev/null || echo {}"
        # Does the Service route to a listener at all, separately from whether
        # any camera works? A 2xx from /api/streams proves the Service, the
        # endpoint and go2rtc's own API; its *body* goes to /dev/null for the
        # reason above.
        "printf '\n--- apireachable\n'"
        "$kubectl get --raw $rawBase/api/streams --request-timeout=10s >/dev/null 2>&1 && echo ok || echo unreachable"
        "printf '\n--- registered\n'"
        $registeredLine
        # A frame per registered stream, and the reason this script is worth
        # having. Producing one means go2rtc opened an RTSP session from inside
        # the pod network to the camera's address, the credential Aerie sent it
        # was accepted, and a keyframe arrived and decoded. That is the plan's
        # pod-egress item and its in-cluster frame item in one request.
        #
        # --request-timeout is generous on purpose: a cold stream waits for the
        # camera's next IDR, which on the measured Reolink sub-stream is up to
        # 4 s all by itself, on top of the RTSP connect.
        "printf '\n--- frames\n'"
        $framesLine
        "printf '\n--- end\n'"
    ) -join '; '

    # No overall timeout here on purpose: Invoke-NodeSsh has none to give, and
    # every request inside the probe already carries its own --request-timeout,
    # so the worst case is bounded by the number of streams rather than open
    # ended.
    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 30
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "The probe against $IPAddress failed as a whole$(if ($permanentReason) { ": $permanentReason" }). Nothing was checked:`n$($probe.StdErr)"
    }
    if ($probe.StdOut -notmatch '--- end') {
        throw "The probe against $IPAddress did not run to completion - its final marker is missing, so an unknown number of sections below are truncated rather than empty. Raw output:`n$($probe.StdOut)"
    }
    Write-Host "Collected $((@($probe.StdOut -split '--- ')).Count - 1) section(s) in one round trip."

    $deployment = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'deployment'
    $pods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pods'))
    $streamNames = @((Get-ProbeSection -Output $probe.StdOut -Name 'registered') -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $apiReachable = (Get-ProbeSection -Output $probe.StdOut -Name 'apireachable').Trim() -eq 'ok'
    $frames = @{}
    foreach ($line in ((Get-ProbeSection -Output $probe.StdOut -Name 'frames') -split "`r?`n")) {
        if ($line -match '^(\S+)\s+(\d+)\s*$') { $frames[$Matches[1]] = [int]$Matches[2] }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- The arrangement that makes registration work ------------------ #

    # This is the check that exists because of a bug that hides itself.
    # go2rtc persists a runtime `PUT /api/streams` to its *first* -config path
    # and answers 400 when that path is read-only - while still registering the
    # stream. So a chart that mounts every config read-only produces working
    # video and a stream of registration errors in Aerie's log, which is a
    # combination nobody diagnoses quickly. The order below is what keeps that
    # from happening, and it reads backwards, so it is asserted rather than
    # trusted to survive the next edit.
    $container = @(Get-Path $deployment 'spec.template.spec.containers' | Where-Object { $_ }) |
        Where-Object { (Get-Field $_ 'name') -eq 'go2rtc' } | Select-Object -First 1
    $containerArgs = @(Get-Field $container 'args' | Where-Object { $_ })
    $firstConfig = $null
    for ($i = 0; $i -lt $containerArgs.Count - 1; $i++) {
        if ($containerArgs[$i] -eq '-config') { $firstConfig = [string]$containerArgs[$i + 1]; break }
    }

    $volumeMounts = @(Get-Field $container 'volumeMounts' | Where-Object { $_ })
    $firstConfigMount = $volumeMounts |
        Where-Object { $firstConfig -and $firstConfig.StartsWith(([string](Get-Field $_ 'mountPath')).TrimEnd('/') + '/', [StringComparison]::Ordinal) } |
        Select-Object -First 1

    if ($null -eq $container) {
        Add-Check -Step '9.config' -Name 'First -config is writable' -Status 'Fail' -Detail "no container named 'go2rtc' in the Deployment"
    }
    elseif ($null -eq $firstConfig) {
        Add-Check -Step '9.config' -Name 'First -config is writable' -Status 'Fail' -Detail 'no -config argument found, so go2rtc is running on defaults'
    }
    elseif ($null -eq $firstConfigMount) {
        Add-Check -Step '9.config' -Name 'First -config is writable' -Status 'Fail' -Detail "$firstConfig is not under any volumeMount - it is on the container filesystem, which readOnlyRootFilesystem makes unwritable"
    }
    elseif ((Get-Field $firstConfigMount 'readOnly') -eq $true) {
        Add-Check -Step '9.config' -Name 'First -config is writable' -Status 'Fail' -Detail "$firstConfig is mounted readOnly. go2rtc will answer 400 to every stream registration Aerie makes while still registering it, so video works and the log fills with errors"
    }
    else {
        Add-Check -Step '9.config' -Name 'First -config is writable' -Status 'Pass' -Detail "$firstConfig on volume '$(Get-Field $firstConfigMount 'name')'"
    }

    $runtimeVolume = @(Get-Path $deployment 'spec.template.spec.volumes' | Where-Object { $_ }) |
        Where-Object { (Get-Field $_ 'name') -eq [string](Get-Field $firstConfigMount 'name') } | Select-Object -First 1

    if ($null -eq $runtimeVolume) {
        Add-Check -Step '9.config' -Name 'Runtime config is ephemeral' -Status 'Fail' -Detail 'the writable config volume was not found'
    }
    elseif ($null -ne (Get-Field $runtimeVolume 'emptyDir')) {
        # Ephemeral on purpose: Aerie is the source of truth and re-registers
        # before every viewer, so a pod that forgets is correct. A durable
        # volume here would be a second copy of every camera password, on a
        # node, going stale.
        Add-Check -Step '9.config' -Name 'Runtime config is ephemeral' -Status 'Pass' -Detail 'emptyDir - go2rtc forgets on restart, which is the intended lifetime'
    }
    else {
        Add-Check -Step '9.config' -Name 'Runtime config is ephemeral' -Status 'Fail' -Detail "volume '$(Get-Field $runtimeVolume 'name')' is not an emptyDir - a durable one would keep a stale copy of every camera password on a node"
    }

    # --- go2rtc itself ------------------------------------------------ #

    if ($null -eq $deployment -or -not (Get-Path $deployment 'metadata.name')) {
        Add-Check -Step '9.go2rtc' -Name "Deployment $Go2RtcDeployment" -Status 'Fail' -Detail "not found in namespace $Namespace"
    }
    else {
        $desired = [int](Get-Path $deployment 'spec.replicas')
        $available = [int](Get-Path $deployment 'status.availableReplicas')
        if ($available -ge 1 -and $available -eq $desired) {
            Add-Check -Step '9.go2rtc' -Name "Deployment $Go2RtcDeployment" -Status 'Pass' -Detail "$available/$desired available"
        }
        else {
            Add-Check -Step '9.go2rtc' -Name "Deployment $Go2RtcDeployment" -Status 'Fail' -Detail "$available/$desired available"
        }
    }

    if ($pods.Count -eq 0) {
        Add-Check -Step '9.go2rtc' -Name 'go2rtc pod' -Status 'Fail' -Detail "no pods matched $Go2RtcComponentLabel"
    }
    else {
        # Recreate strategy, one replica: more than one pod means a rollout is
        # in flight, and two go2rtc processes are two RTSP clients per camera -
        # the thing the Deployment's strategy exists to prevent overlapping.
        $ready = @($pods | Where-Object { (Get-Field (Get-Condition $_ 'Ready') 'status') -eq 'True' })
        if ($ready.Count -eq 1 -and $pods.Count -eq 1) {
            Add-Check -Step '9.go2rtc' -Name 'go2rtc pod' -Status 'Pass' -Detail "$(Get-Path $ready[0] 'metadata.name') Ready on $(Get-Path $ready[0] 'spec.nodeName')"
        }
        elseif ($pods.Count -gt 1) {
            Add-Check -Step '9.go2rtc' -Name 'go2rtc pod' -Status 'Fail' -Detail "$($pods.Count) pods ($($ready.Count) Ready) - a rollout is in flight, or Recreate was overridden; each one opens its own RTSP session to every camera"
        }
        else {
            Add-Check -Step '9.go2rtc' -Name 'go2rtc pod' -Status 'Fail' -Detail "$(Get-Path $pods[0] 'metadata.name') is $(Get-Path $pods[0] 'status.phase') and not Ready"
        }
    }

    if ($apiReachable) {
        Add-Check -Step '9.go2rtc' -Name "Service $Go2RtcService answers" -Status 'Pass' -Detail "2xx from /api/streams via the API server's service proxy (body deliberately not read - it returns every stream's source URL, passwords included)"
    }
    else {
        Add-Check -Step '9.go2rtc' -Name "Service $Go2RtcService answers" -Status 'Fail' -Detail "no 2xx from ${Go2RtcService}:$Go2RtcPort/api/streams. The pod may be Ready without the Service selecting it - compare the Service's selector with the pod's labels"
    }

    # --- The camera, end to end --------------------------------------- #

    # Conditional, and that is a consequence of the design rather than a gap in
    # this script. Aerie registers a stream lazily - immediately before it
    # relays one - so an idle go2rtc holds none, and "no streams registered"
    # means "nobody has watched a camera since this pod started", not "no
    # cameras are configured". A gate cannot manufacture the missing viewer
    # either: registering a stream from here would put a camera password on a
    # command line, where any other user's `ps` can read it for as long as the
    # command runs, which is the one thing Invoke-NodeSsh exists to refuse.
    #
    # So this reports what it can see. Open a camera in the admin UI or walk in
    # front of one, then re-run, and the egress proof below is available.
    if ($streamNames.Count -eq 0) {
        Add-Check -Step '9.frame' -Name 'Live frame' -Status 'Warn' -Detail 'no streams registered - nothing has watched a camera since this go2rtc started, so the pod-to-camera hop was not exercised. Open a camera in the admin UI and re-run'
    }

    # One check per stream rather than one for all of them: with two cameras
    # and one unreachable, "1 of 2" is the answer, and which one matters.
    foreach ($name in $streamNames) {
        $bytes = if ($frames.ContainsKey($name)) { $frames[$name] } else { 0 }
        if ($bytes -ge $MinimumFrameBytes) {
            Add-Check -Step '9.frame' -Name "Live frame $name" -Status 'Pass' -Detail "$bytes byte JPEG - the pod reached this camera's RTSP port, the credential Aerie sent was accepted, and a keyframe decoded"
        }
        else {
            Add-Check -Step '9.frame' -Name "Live frame $name" -Status 'Fail' -Detail "$bytes byte(s), want at least $MinimumFrameBytes. Either the pod network cannot reach the camera (the plan's one design-changing item - check the CNI and any LAN firewall rule for pod CIDR -> camera:554), the credential set for this camera in the devices admin UI is wrong, or the camera is off"
        }
    }
}
finally {
    if ($tempKeyFile -and (Test-Path $tempKeyFile)) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile -and (Test-Path $knownHostsFile)) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
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

# What this gate cannot answer, named on every run rather than only in the
# architecture doc - a green table is otherwise an invitation to believe
# cameras are done.
$stillManual = @(
    'Walk in front of a camera: the kiosk modal opens with live video and closes when motion ends.'
    'The X closes it, and the *next* motion event reopens it (the per-event dismissal rule).'
    'MediaSource works in the kiosk Android WebView specifically, not just in Chrome.'
    'Motion -> first frame, measured. Tune the camera GOP first or you are measuring the camera.'
    'A camera added in the devices admin UI streams without touching the cluster - which is the whole point of the form.'
)

if ($env:GITHUB_STEP_SUMMARY) {
    $tick = [char]0x60
    $lines = @(
        "## Camera bring-up gate - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { 'PASS' } 'Warn' { 'WARN' } default { 'FAIL' } }
        $detail = ($check.Detail -replace '\|', '\|')
        $lines += "| $($check.Step) | $($check.Check) | $mark | $detail |"
    }
    $lines += @(
        ''
        '### Still needs a person'
        ''
    )
    foreach ($item in $stillManual) { $lines += "- $item" }
    $lines += @(
        ''
        '_See docs/camera-devices-architecture.md. Read-only, and no stream source is'
        '_printed - go2rtc returns camera passwords in its own API._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Camera bring-up gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. docs/camera-devices-architecture.md has the'
    Write-Host 'reasoning behind each check.'
    exit 1
}

Write-Host "Camera bring-up gate passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
if ($warned -gt 0) {
    Write-Host "$warned advisory warning(s) above - they do not fail the gate, and each says why."
}
Write-Host ''
Write-Host 'The cluster half only. Still needs a person:'
foreach ($item in $stillManual) { Write-Host "  - $item" }
exit 0
