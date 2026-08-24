<#
.SYNOPSIS
    Asserts the cluster half of the cameras plan's first-camera bring-up
    (docs/plans/cameras.md Phase 9), in the shape scripts/k3s/Test-DataTier.ps1
    established for a phase gate: read-only, one round trip, one table.

.DESCRIPTION
    Phase 9 splits cleanly in two. One half needs a person - walking in front
    of the camera, watching a modal open on a wall tablet, judging whether the
    feed feels live. That half stays in the plan. The other half is a set of
    yes/no facts about a cluster: is the streams Secret there, does go2rtc hold
    it, and - the item the plan flagged as the one that could force a design
    change - can a *pod* reach a camera's RTSP port at all.

    This is that other half, as a command. It exists because those facts are
    re-asked every time a camera is added, not once at bring-up: a second
    camera is a new line in the same Secret and a rollout restart somebody has
    to remember, and the failure when they don't is a feed that never opens.

    Three properties, the same three Test-DataTier.ps1 states:

    **It does not stop at the first failure.** Nothing here writes anything.

    **A check it cannot evaluate is a failure, not a skip.** An absent object
    or an unreadable response is "not proven", and a gate that reports that as
    a skip can be satisfied by a cluster that is switched off.

    **Expectations come from the repository and the cluster, not from
    parameters.** Which namespace, Secret and key to look in comes from
    scripts/secrets/parameters.json - the same file that generated the
    ExternalSecret - and which streams to prove comes from the Secret itself.
    There is no camera name to pass and none to keep up to date here.

    **No credential leaves the node.** That constraint shapes the probe more
    than anything else. A go2rtc stream source is an RTSP URL with the camera's
    password in it, `GET /api/streams` returns those URLs verbatim, and this
    script's output is a CI run log. So the probe never prints a stream source
    and never prints /api/streams: it decodes the Secret on the node, pipes it
    straight into a filter that keeps only the text left of the first colon,
    and prints stream *names*. Everything else is a byte count.

    Stages:
      1. Preflight - the SSH key resolves, the client is present, the node
                     answers 22, and parameters.json parses.
      2. Probe     - one round trip: the ExternalSecret, the Secret's key
                     size, the stream names, the Deployment, the pods, and one
                     JPEG frame per stream fetched through the API server's
                     service proxy.
      3. Checks    - evaluated against that snapshot.
      4. Report    - one table, one exit code.

    What a green run does and does not prove. It proves the whole camera path
    up to the browser: ESO holds the streams file, go2rtc is running with it,
    the pod network reaches each camera's RTSP port, the credential in the file
    is accepted, and the camera is producing decodable video. It says nothing
    about HA's motion events, the SSE hop, or whether MediaSource works in the
    kiosk's WebView - those are the items the plan keeps for a human, and the
    report repeats which ones they are.

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

    [string]$ParametersPath = (Join-Path $PSScriptRoot '..\secrets\parameters.json'),

    # The parameter whose 'kubernetes' block says where the streams file
    # lands. Named rather than hardcoded so this script reads the same source
    # New-ExternalSecrets.ps1 renders from - rename the key there and this
    # follows, or fails preflight saying so.
    [string]$StreamsParameterKey = 'cameras/go2rtc-streams',

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

    if (-not (Test-Path $ParametersPath -PathType Leaf)) {
        $failures.Add("Not found: '$ParametersPath'. Run this from a checkout of the repository the cluster reconciles from - the namespace, Secret and key below are read from it rather than hardcoded here.")
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

    # Where the streams file lands, from the map rather than from memory. A
    # gate that hardcodes this passes happily against a cluster whose Secret
    # was renamed out from under it, which is the one thing it exists to
    # notice.
    $streamsNamespace = $null
    $streamsSecretName = $null
    $streamsSecretKey = $null
    if (Test-Path $ParametersPath -PathType Leaf) {
        try {
            $parameters = Get-Content -Path $ParametersPath -Raw | ConvertFrom-Json
            $entry = @(Get-Field $parameters 'parameters' | Where-Object { (Get-Field $_ 'key') -eq $StreamsParameterKey }) | Select-Object -First 1
            $target = Get-Field $entry 'kubernetes'
            # 'kubernetes' may be an array - see parameters.json. The streams
            # file lands in exactly one namespace, so the first block is the
            # block, but taking [0] rather than assuming a scalar keeps this
            # working if that ever changes.
            if ($target -is [array]) { $target = @($target)[0] }
            $streamsNamespace = [string](Get-Field $target 'namespace')
            $streamsSecretName = [string](Get-Field $target 'secretName')
            $streamsSecretKey = [string](Get-Field $target 'secretKey')
            if (-not $streamsNamespace -or -not $streamsSecretName -or -not $streamsSecretKey) {
                $failures.Add("'$StreamsParameterKey' in $ParametersPath has no complete 'kubernetes' block, so there is nothing to look for. Add one and regenerate: pwsh ./scripts/secrets/New-ExternalSecrets.ps1")
            }
        }
        catch { $failures.Add("$ParametersPath did not parse as JSON: $($_.Exception.Message)") }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    # The ExternalSecret's name is its target Secret's name - that is how
    # New-ExternalSecrets.ps1 names every file it renders.
    $externalSecretName = $streamsSecretName

    Write-Host "Cluster:  $IPAddress"
    Write-Host "Repo:     $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    Write-Host "Streams:  Secret '$streamsSecretName' key '$streamsSecretKey' in namespace '$streamsNamespace' (from $StreamsParameterKey)"
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
    # The stream names come out of the Secret, not out of go2rtc's
    # /api/streams. Both know the same names; only one of them also returns
    # every camera's password in the same response, and this output is a CI
    # log. The decode happens inside a pipeline on the node and its result is
    # never a word the shell prints: `cut -d: -f1` keeps the text left of the
    # first colon, which for `camera.x: rtsp://user:pass@host/path` is
    # `camera.x` and for a list-form value is the same. A `- rtsp://...`
    # continuation line does not match the grep at all.
    # jsonpath treats a dot as a path separator, so a key that contains one
    # has to escape it - and the shell eats one level of backslash on the way
    # through, so what is written here is doubled to arrive as `\.`. This is
    # the whole cost of `streams.yaml` being a filename rather than a name.
    $jsonPathKey = $streamsSecretKey -replace '\.', '\\.'
    $kubectl = "sudo k3s kubectl -n $streamsNamespace"
    $rawBase = "/api/v1/namespaces/$streamsNamespace/services/${Go2RtcService}:$Go2RtcPort/proxy"

    # Decode on the node, filter on the node, print names. `cut -d: -f1` keeps
    # the text left of the first colon, which for `camera.x: rtsp://u:p@h/s`
    # is `camera.x`; a `- rtsp://...` continuation line does not match the
    # grep at all, so no shape of this file leaks a source URL.
    #
    # `[:blank:]`, not `[:space:]`: the class has to be space and tab only.
    # `[:space:]` includes the newline, so `tr` would run every stream name
    # into the one before it - one word, no error, and a frame loop that
    # fetches a stream nobody configured. Caught by running the probe against
    # a stub node with a two-camera file, which is the only way this shows up.
    $streamNamesPipeline = "$kubectl get secret $streamsSecretName -o jsonpath={.data.$jsonPathKey} 2>/dev/null" +
        " | base64 -d 2>/dev/null" +
        " | grep -E '^[[:space:]]+[A-Za-z0-9_.-]+:'" +
        " | cut -d: -f1 | tr -d '[:blank:]'"

    # The frame loop, built by concatenation rather than interpolation: `$(`
    # and `$S` are the *shell's* dollars, and a double-quoted PowerShell
    # string would consume them before ssh ever saw them.
    $framesLine = 'for S in $(' + $streamNamesPipeline + '); do printf ''%s '' $S; ' +
        $kubectl + ' get --raw ' + $rawBase + '/api/frame.jpeg?src=$S --request-timeout=25s 2>/dev/null | wc -c; done'

    $probeScript = @(
        'set -f'
        "printf '\n--- externalsecret\n'"
        "$kubectl get externalsecrets.external-secrets.io $externalSecretName -o json 2>/dev/null || echo {}"
        "printf '\n--- deployment\n'"
        "$kubectl get deployment $Go2RtcDeployment -o json 2>/dev/null || echo {}"
        "printf '\n--- pods\n'"
        "$kubectl get pods -l $Go2RtcComponentLabel -o json 2>/dev/null || echo {}"
        # Byte count of the key's value, never the value. `wc -c` runs on the
        # node, so what crosses the wire is a number.
        "printf '\n--- secretkey\n'"
        "printf '$streamsSecretKey '; $kubectl get secret $streamsSecretName -o jsonpath={.data.$jsonPathKey} 2>/dev/null | wc -c"
        "printf '\n--- streamnames\n'"
        $streamNamesPipeline
        # Does the Service route to a listener at all, separately from whether
        # any camera works? A 2xx from /api/streams proves the Service, the
        # endpoint and go2rtc's own API; its *body* is the one response in
        # this system that must never be logged, so it goes to /dev/null and
        # only the outcome is printed.
        "printf '\n--- apireachable\n'"
        "$kubectl get --raw $rawBase/api/streams --request-timeout=10s >/dev/null 2>&1 && echo ok || echo unreachable"
        # The frame per stream, and the reason this script is worth having.
        # Producing one means go2rtc opened an RTSP session from inside the
        # pod network to the camera's address, the credential in the streams
        # file was accepted, and a keyframe arrived and decoded. That is the
        # plan's pod-egress item and its in-cluster frame item in one request.
        #
        # --request-timeout is generous on purpose: a cold stream waits for
        # the camera's next IDR, which on the measured Reolink sub-stream is
        # up to 4 s all by itself, on top of the RTSP connect.
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

    $externalSecret = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'externalsecret'
    $deployment = ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'deployment'
    $pods = @(Get-Items (ConvertFrom-ProbeJson -Output $probe.StdOut -Name 'pods'))
    # "<key> <byte count>" - `wc -c` pads with leading spaces on some
    # platforms, so match the trailing digits rather than a fixed column.
    $secretKeyBytes = 0
    if ((Get-ProbeSection -Output $probe.StdOut -Name 'secretkey') -match '\s(\d+)\s*$') { $secretKeyBytes = [int]$Matches[1] }
    $streamNames = @((Get-ProbeSection -Output $probe.StdOut -Name 'streamnames') -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $apiReachable = (Get-ProbeSection -Output $probe.StdOut -Name 'apireachable').Trim() -eq 'ok'
    $frames = @{}
    foreach ($line in ((Get-ProbeSection -Output $probe.StdOut -Name 'frames') -split "`r?`n")) {
        if ($line -match '^(\S+)\s+(\d+)\s*$') { $frames[$Matches[1]] = [int]$Matches[2] }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    # --- The streams Secret ------------------------------------------ #

    $readyCondition = Get-Condition $externalSecret 'Ready'
    if ($null -eq $externalSecret -or -not (Get-Path $externalSecret 'metadata.name')) {
        Add-Check -Step '9.secret' -Name "ExternalSecret $externalSecretName" -Status 'Fail' -Detail "not found in namespace $streamsNamespace - the manifest is generated from parameters.json, so either the commit hasn't reconciled or infra-config is NotReady"
    }
    elseif ((Get-Field $readyCondition 'status') -eq 'True') {
        Add-Check -Step '9.secret' -Name "ExternalSecret $externalSecretName" -Status 'Pass' -Detail (Format-Condition $readyCondition -MaxLength 60)
    }
    else {
        Add-Check -Step '9.secret' -Name "ExternalSecret $externalSecretName" -Status 'Fail' -Detail "$(Format-Condition $readyCondition) - SecretSyncError here usually means the parameter was never seeded: run Provision 2 with GO2RTC_STREAMS set"
    }

    if ($secretKeyBytes -gt 0) {
        Add-Check -Step '9.secret' -Name "Secret $streamsSecretName key $streamsSecretKey" -Status 'Pass' -Detail "$secretKeyBytes base64 byte(s) - the key is a filename, because the Deployment mounts this Secret as a directory"
    }
    else {
        Add-Check -Step '9.secret' -Name "Secret $streamsSecretName key $streamsSecretKey" -Status 'Fail' -Detail "absent or empty. go2rtc skips a -config path that does not exist and stays Ready with zero streams, so this fails silently everywhere except here"
    }

    if ($streamNames.Count -eq 0) {
        Add-Check -Step '9.secret' -Name 'Streams declared' -Status 'Fail' -Detail 'no stream names found in the streams file. Expected one `  <entity id>: <rtsp url>` line per camera under a `streams:` key'
    }
    else {
        Add-Check -Step '9.secret' -Name 'Streams declared' -Status 'Pass' -Detail "$($streamNames.Count): $($streamNames -join ', ')"
    }

    # CameraStreamTarget builds go2rtc's URL from the CameraFeed channel's
    # HaEntityId verbatim, so a stream named anything else is a stream the
    # kiosk can never ask for. Nothing in go2rtc objects - it takes an
    # arbitrary string as a name - which is exactly why it is worth asserting.
    $misnamed = @($streamNames | Where-Object { $_ -notmatch '^camera\.[a-z0-9_]+$' })
    if ($streamNames.Count -eq 0) {
        Add-Check -Step '9.secret' -Name 'Stream names are HA entity ids' -Status 'Fail' -Detail 'no streams to check'
    }
    elseif ($misnamed.Count -eq 0) {
        Add-Check -Step '9.secret' -Name 'Stream names are HA entity ids' -Status 'Pass' -Detail "all $($streamNames.Count) look like camera.<entity>"
    }
    else {
        Add-Check -Step '9.secret' -Name 'Stream names are HA entity ids' -Status 'Fail' -Detail "$($misnamed -join ', ') - CameraStreamTarget asks go2rtc for the CameraFeed channel's HaEntityId verbatim, so a stream under any other name is unreachable from the kiosk"
    }

    # --- go2rtc itself ------------------------------------------------ #

    if ($null -eq $deployment -or -not (Get-Path $deployment 'metadata.name')) {
        Add-Check -Step '9.go2rtc' -Name "Deployment $Go2RtcDeployment" -Status 'Fail' -Detail "not found in namespace $streamsNamespace"
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

    # One check per stream rather than one for all of them: with two cameras
    # and one unreachable, "1 of 2" is the answer, and which one matters.
    if ($streamNames.Count -eq 0) {
        Add-Check -Step '9.frame' -Name 'Live frame' -Status 'Fail' -Detail 'no streams declared, so nothing was fetched'
    }
    foreach ($name in $streamNames) {
        $bytes = if ($frames.ContainsKey($name)) { $frames[$name] } else { 0 }
        if ($bytes -ge $MinimumFrameBytes) {
            Add-Check -Step '9.frame' -Name "Live frame $name" -Status 'Pass' -Detail "$bytes byte JPEG - the pod reached this camera's RTSP port, the credential was accepted, and a keyframe decoded"
        }
        else {
            Add-Check -Step '9.frame' -Name "Live frame $name" -Status 'Fail' -Detail "$bytes byte(s), want at least $MinimumFrameBytes. Either the pod network cannot reach the camera (the plan's one design-changing item - check the CNI and any LAN firewall rule for pod CIDR -> camera:554), the credential in the streams file is wrong, the camera is off, or go2rtc is running an older streams file and has never heard of this name: kubectl rollout restart deploy/$Go2RtcDeployment -n $streamsNamespace"
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

# What this gate cannot answer, named every run rather than only in the plan -
# a green table is otherwise an invitation to believe cameras are done.
$stillManual = @(
    'Walk in front of a camera: the kiosk modal opens with live video and closes when motion ends.'
    'The X closes it, and the *next* motion event reopens it (the per-event dismissal rule).'
    'MediaSource works in the kiosk Android WebView specifically, not just in Chrome.'
    'Motion -> first frame, measured. Tune the camera GOP first or you are measuring the camera.'
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
        '_docs/plans/cameras.md Phase 9. Read-only, and no stream source is printed - go2rtc returns camera passwords in its own API._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Camera bring-up gate FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host 'Nothing was changed. docs/plans/cameras.md Phase 9 has the reasoning behind each check.'
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
