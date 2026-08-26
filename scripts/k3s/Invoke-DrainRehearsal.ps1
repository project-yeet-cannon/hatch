<#
.SYNOPSIS
    Cordons and drains one node while measuring whether the house notices,
    then puts it back. docs/plans/part-time-node.md 2.4 - the dress rehearsal
    for every future personal-mode entry, run against a node whose owner is
    not waiting to play a game.

.DESCRIPTION
    Phase 2's exit criterion is "`kubectl drain` of any node completes without
    `--force`, and ingress and DNS survive it with no gap". That is a claim
    about a running cluster, so this is the thing that makes it.

    **It is also where three guessed numbers get measured.** 4.1's 90-second
    drain deadline is explicitly a guess in the plan, and two of the things
    inside it can only be timed by watching them happen:

      - how long Longhorn takes to drop the per-node `instance-manager` PDB.
        Its `node-drain-policy` is `block-if-contains-last-replica` and those
        PDBs are the mechanism it blocks *with*: the node controller removes
        one once the node is cordoned and it has satisfied itself no volume's
        last healthy replica is at stake. A standing zero on an uncordoned
        node is the resting state, not a blocker - but "Longhorn takes the PDB
        away when asked" is a claim worth watching rather than trusting.
      - how long CNPG's switchover takes when the drained node holds a
        primary. The operator watches for the cordon and moves the primary,
        after which the PDB selects a pod on another node and the drain
        proceeds. It is the step most likely to dominate a personal-mode
        entry's budget.

    ## What it measures continuity with, and why from the node

    Two probes, both run **on a k3s server** rather than from a pod:

      - **Ingress.** `curl` to the ingress VIP on 443, once a second. Any HTTP
        status at all is a pass - a 404 from Traefik proves Traefik answered,
        which is the question. A connection failure is the gap.
      - **DNS.** A lookup of `kubernetes.default.svc.cluster.local` against
        CoreDNS's ClusterIP, once a second. A node can reach a ClusterIP
        through the same kube-proxy rules a pod does, so this needs no pod and
        no image pull - which matters, because a rehearsal that depended on
        scheduling something during a drain would be measuring its own
        scaffolding.

    The DNS tool is whichever of `dig`, `nslookup` or `busybox nslookup` the
    node actually has. None of the three is a failure rather than a skip: a
    rehearsal that could not have detected a DNS gap must not report that
    there wasn't one.

    ## The gate before it drains anything

    A drain rehearsed against a single Traefik replica measures the old
    cluster, so this refuses to start unless Traefik and CoreDNS each have at
    least two Ready pods on distinct nodes - which is 2.1 and 2.2. Override
    with -ProceedWithSingletons when the point *is* to measure the gap a
    singleton produces; the report says so either way, and the measured gap is
    then a number about a known single point of failure rather than a
    surprise.

    ## What it does to the cluster

    One cordon, one drain, one uncordon. Nothing is deleted, nothing is
    scaled, and the uncordon runs from both the remote script's own exit path
    and this script's `finally` - a rehearsal that left a node cordoned would
    be worse than not rehearsing.

    Same three properties as every gate here: it does not stop at the first
    failure, a check it cannot evaluate is a failure rather than a skip, and
    expectations come from the cluster rather than from numbers written down
    in this file.

    Stages:
      1. Preflight - the key resolves, the node answers 22, the target node
                     exists and is currently schedulable.
      2. Gate      - Traefik and CoreDNS are actually redundant, or the
                     override says to proceed anyway.
      3. Rehearse  - one remote script: start the probes, cordon, drain,
                     observe, uncordon, print everything it saw.
      4. Checks    - evaluated against that output.
      5. Report    - one table, the three numbers 2.4 asks for, one exit code.

.PARAMETER IPAddress
    A k3s **server**'s LAN address. Not the node being drained - the probes
    and the kubectl calls have to keep working while that one is being
    emptied.

.PARAMETER NodeName
    The node to drain. Any permanent node is the point: this is the rehearsal
    that happens before there is a part-time node to rehearse on.

.PARAMETER DrainTimeoutSeconds
    Passed to `kubectl drain --timeout`. The rehearsal's own deadline, and the
    number 4.1 starts at. A drain that needs longer than this is the finding.

.EXAMPLE
    .\Invoke-DrainRehearsal.ps1 -IPAddress 192.168.1.240 -NodeName aerie-node-2 `
        -SshPrivateKeyPath ~\.ssh\aerie_node

.EXAMPLE
    # Measure what a singleton CoreDNS actually costs, deliberately.
    .\Invoke-DrainRehearsal.ps1 -IPAddress 192.168.1.240 -NodeName aerie-node-2 `
        -ProceedWithSingletons -SshPrivateKeyPath ~\.ssh\aerie_node
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [Parameter(Mandatory)]
    [string]$NodeName,

    [int]$DrainTimeoutSeconds = 90,

    # How long after the drain to keep probing. Ingress and DNS can be
    # disturbed by the rescheduling that *follows* an eviction rather than by
    # the eviction, and a probe that stopped at the drain's last second would
    # miss exactly that.
    [int]$SettleSeconds = 30,

    [switch]$ProceedWithSingletons,

    [string]$Username = 'aerie',
    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# Phase 2's own subjects. Named here because the gate reads them, not because
# anything about them is configurable - two replicas of each on distinct nodes
# is what 2.1 and 2.2 are.
$RedundancyTargets = @(
    [pscustomobject]@{ Namespace = 'kube-system'; Selector = 'app.kubernetes.io/name=traefik'; Name = 'traefik' }
    [pscustomobject]@{ Namespace = 'kube-system'; Selector = 'k8s-app=kube-dns'; Name = 'coredns' }
)

function Write-Stage {
    param([Parameter(Mandatory)][string]$Name)
    Write-Host ''
    Write-Host "== $Name " -NoNewline -ForegroundColor Cyan
    Write-Host ('=' * [math]::Max(0, 60 - $Name.Length)) -ForegroundColor Cyan
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
    Write-Host ("  [{0}] {1,-10} {2}{3}" -f $marker, $Step, $Name, $(if ($Detail) { " - $Detail" })) -ForegroundColor $colour
}

function Get-ProbeSection {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Output, [Parameter(Mandatory)][string]$Name)
    $collected = New-Object Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in @($Output -split "`r?`n")) {
        if ($line.TrimEnd() -eq "--- $Name") { $inSection = $true; continue }
        if ($line -match '^--- \S+$') { if ($inSection) { break }; continue }
        if ($inSection) { $collected.Add($line) }
    }
    return ($collected -join "`n").Trim()
}

# ------------------------------------------------------------------ #
# The remote rehearsal.
# ------------------------------------------------------------------ #
#
# One script rather than a conversation of round trips, for the reason every
# gate in this directory is built that way: the interesting events happen
# inside a window of tens of seconds, and a probe whose sample interval is a
# Windows-to-Linux SSH round trip cannot see them. Everything below runs on
# the server, at its own pace, and hands back one transcript to be read.
#
# Placeholders are @@TOKEN@@ rather than PowerShell's -f, because bash is full
# of braces and a format string would fight it on every line.
$RemoteScript = @'
set -u
K='sudo k3s kubectl'
NODE='@@NODE@@'
DRAIN_TIMEOUT=@@DRAIN_TIMEOUT@@
SETTLE=@@SETTLE@@
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

now() { date +%s.%N; }

echo "--- DISCOVERY"
VIP=$($K -n flux-system get configmap aerie-cluster-config -o jsonpath='{.data.INGRESS_VIP}' 2>/dev/null || true)
DNSIP=$($K -n kube-system get svc kube-dns -o jsonpath='{.spec.clusterIP}' 2>/dev/null || true)
echo "vip=$VIP"
echo "dnsip=$DNSIP"

# Whichever resolver this node happens to have. Reported rather than assumed,
# so a run with none of the three is a run that says so instead of a run that
# quietly reports no DNS gap because it never looked.
DNSTOOL=none
if command -v dig >/dev/null 2>&1; then DNSTOOL=dig
elif command -v nslookup >/dev/null 2>&1; then DNSTOOL=nslookup
elif command -v busybox >/dev/null 2>&1; then DNSTOOL=busybox
fi
echo "dnstool=$DNSTOOL"
command -v curl >/dev/null 2>&1 && echo "curl=yes" || echo "curl=no"

echo "--- BASELINE"
echo "# nodes"
$K get nodes -o wide --no-headers 2>&1 || true
echo "# pods on the target"
$K get pods -A --field-selector spec.nodeName=$NODE -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name,OWNER:.metadata.ownerReferences[0].kind --no-headers 2>&1 || true
echo "# pdbs"
$K get pdb -A -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name,ALLOWED:.status.disruptionsAllowed --no-headers 2>&1 || true

dns_once() {
  case "$DNSTOOL" in
    dig)      dig +short +time=2 +tries=1 @"$DNSIP" kubernetes.default.svc.cluster.local >/dev/null 2>&1 ;;
    nslookup) nslookup kubernetes.default.svc.cluster.local "$DNSIP" >/dev/null 2>&1 ;;
    busybox)  busybox nslookup kubernetes.default.svc.cluster.local "$DNSIP" >/dev/null 2>&1 ;;
    *)        return 127 ;;
  esac
}

# --- the probes, each in its own background loop -------------------------- #
(
  while :; do
    T=$(now)
    if [ -n "$VIP" ]; then
      CODE=$(curl -sk -o /dev/null -m 2 -w '%{http_code}' "https://$VIP/" 2>/dev/null || echo 000)
    else
      CODE=skip
    fi
    echo "$T $CODE" >> "$WORK/ingress"
    sleep 1
  done
) & INGRESS_PID=$!

(
  while :; do
    T=$(now)
    if dns_once; then R=ok; else R=fail; fi
    echo "$T $R" >> "$WORK/dns"
    sleep 1
  done
) & DNS_PID=$!

# --- the observer: what the drain is waiting on, sampled ------------------ #
(
  while :; do
    T=$(now)
    $K get pdb -A -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name,ALLOWED:.status.disruptionsAllowed --no-headers 2>/dev/null \
      | while read -r ns name allowed; do echo "$T pdb $ns/$name $allowed"; done >> "$WORK/timeline"
    $K get pods -A -l cnpg.io/instanceRole=primary -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name,NODE:.spec.nodeName --no-headers 2>/dev/null \
      | while read -r ns name node; do echo "$T cnpg $ns/$name $node"; done >> "$WORK/timeline"
    echo "$T nodestatus $($K get node $NODE --no-headers 2>/dev/null | awk '{print $2}')" >> "$WORK/timeline"
    sleep 2
  done
) & OBSERVER_PID=$!

# A few seconds of quiet first, so every measurement below has a "before" to
# be compared against rather than starting mid-event.
sleep 6

echo "--- DRAIN"
T_CORDON=$(now)
echo "cordon_at=$T_CORDON"
$K cordon "$NODE" 2>&1 || true

T_DRAIN=$(now)
echo "drain_at=$T_DRAIN"
# No --force. A pod no controller owns would be deleted with nothing to
# recreate it, and a rehearsal that could destroy something is a worse risk
# than the one it retires.
$K drain "$NODE" --ignore-daemonsets --delete-emptydir-data --timeout=${DRAIN_TIMEOUT}s 2>&1
DRAIN_EXIT=$?
T_DONE=$(now)
echo "drain_exit=$DRAIN_EXIT"
echo "drain_done_at=$T_DONE"

# Keep probing past the drain: rescheduling is what disturbs ingress and DNS,
# and it happens after the eviction rather than during it.
sleep "$SETTLE"

echo "--- UNCORDON"
$K uncordon "$NODE" 2>&1 || true
T_UNCORDON=$(now)
echo "uncordon_at=$T_UNCORDON"

kill $INGRESS_PID $DNS_PID $OBSERVER_PID 2>/dev/null || true
wait $INGRESS_PID $DNS_PID $OBSERVER_PID 2>/dev/null || true

echo "--- INGRESS"
cat "$WORK/ingress" 2>/dev/null || true

echo "--- DNS"
cat "$WORK/dns" 2>/dev/null || true

echo "--- TIMELINE"
cat "$WORK/timeline" 2>/dev/null || true

echo "--- AFTER"
$K get nodes -o wide --no-headers 2>&1 || true
echo "# pods still on the target"
$K get pods -A --field-selector spec.nodeName=$NODE -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name --no-headers 2>&1 || true

echo "--- END"
'@

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$ssh = $null

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]
    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) { $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey.') }
    try { Assert-OpenSshClient } catch { $failures.Add($_.Exception.Message) }
    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) { $failures.Add("$IPAddress does not answer on 22.") }
    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        throw "Preflight failed with $($failures.Count) problem(s). Nothing was touched."
    }

    $resolved = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'drain-rehearsal'
    $tempKeyFile = $resolved.TempFile
    $knownHostsFile = Join-Path $env:TEMP "aerie-drain-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $resolved.Path; KnownHostsFile = $knownHostsFile }

    $nodeProbe = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl get node $NodeName --no-headers"
    if ($nodeProbe.ExitCode -ne 0) {
        throw "The apiserver at $IPAddress could not be asked about node '$NodeName': $(($nodeProbe.StdErr + $nodeProbe.StdOut) -replace '\s+', ' ')"
    }
    $nodeFields = @($nodeProbe.StdOut -split '\s+' | Where-Object { $_ })
    $nodeStatus = if ($nodeFields.Count -ge 2) { $nodeFields[1] } else { 'unreadable' }
    Write-Host "Target node '$NodeName' is $nodeStatus."
    if ($nodeStatus -ne 'Ready') {
        throw "Node '$NodeName' reports '$nodeStatus'. A drain rehearsal against a node that is already unhealthy or already cordoned measures nothing; fix it or pick another node."
    }

    # The node this script is talking *through* must not be the node it is
    # draining - kubectl would be running on a machine whose kubelet is being
    # emptied, and the probes would be measuring their own host.
    $selfProbe = Invoke-NodeSsh @ssh -Command 'hostname'
    if ($selfProbe.StdOut.Trim() -eq $NodeName) {
        throw "-IPAddress $IPAddress is node '$NodeName' itself. Point this at a different server: the probes and the kubectl calls have to keep working while the target is emptied."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Gate'
    # ---------------------------------------------------------------- #

    $singletons = New-Object Collections.Generic.List[string]
    foreach ($target in $RedundancyTargets) {
        $r = Invoke-NodeSsh @ssh -Command (
            "sudo k3s kubectl -n $($target.Namespace) get pods -l $($target.Selector) " +
            '-o custom-columns=NAME:.metadata.name,NODE:.spec.nodeName,READY:.status.containerStatuses[0].ready --no-headers')
        $lines = @($r.StdOut -split "`r?`n" | Where-Object { $_.Trim() })
        $ready = @($lines | Where-Object { $_ -match '\btrue\s*$' })
        $nodes = @($ready | ForEach-Object { @($_ -split '\s+' | Where-Object { $_ })[1] } | Sort-Object -Unique)
        Write-Host ("  {0,-10} {1} ready pod(s) on {2} node(s): {3}" -f $target.Name, $ready.Count, $nodes.Count, ($nodes -join ', '))
        if ($ready.Count -lt 2 -or $nodes.Count -lt 2) {
            $singletons.Add("$($target.Name) has $($ready.Count) ready pod(s) across $($nodes.Count) node(s)")
        }
    }

    if ($singletons.Count -gt 0 -and -not $ProceedWithSingletons) {
        Write-Host ''
        foreach ($s in $singletons) { Write-Host "  - $s" -ForegroundColor Red }
        throw @"
Not draining. $($singletons.Count) of Phase 2's two subjects is still a single point of failure, and a drain rehearsed against a single replica measures the old cluster rather than the one this plan is building.

Land 2.1 (Traefik) and 2.2 (CoreDNS) first, let Flux reconcile them, and confirm two Ready pods on two different nodes.

Re-run with -ProceedWithSingletons if measuring the gap a singleton produces is the point. The report will say that is what happened.
"@
    }
    if ($singletons.Count -gt 0) {
        Write-Host ''
        Write-Warning "-ProceedWithSingletons: draining anyway with $($singletons.Count) known single point(s) of failure. Any gap measured below is a number about that, not a surprise."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Rehearse'
    # ---------------------------------------------------------------- #

    $remote = $RemoteScript.
        Replace('@@NODE@@', $NodeName).
        Replace('@@DRAIN_TIMEOUT@@', "$DrainTimeoutSeconds").
        Replace('@@SETTLE@@', "$SettleSeconds")

    # base64 over -StdIn, then `tr -dc` in front of the decode, for the reason
    # Test-Backup.ps1 spells out at length: -StdIn has been observed to weld a
    # UTF-8 BOM onto what it carries, and here that lands in front of the
    # base64 *text*, where `base64 -d` answers 'invalid input' rather than
    # producing three stray bytes. The filter keeps only the base64 alphabet,
    # so a BOM, a CR, an LF and the trailing newline all fall out.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($remote -replace "`r`n", "`n")))

    $totalWait = 60 + $DrainTimeoutSeconds + $SettleSeconds
    Write-Host "Draining '$NodeName' with a ${DrainTimeoutSeconds}s timeout, then ${SettleSeconds}s of settling. Allow about ${totalWait}s."
    Write-Host 'The node is uncordoned by the remote script and again by this one, so an interrupted run does not leave it cordoned.'

    $run = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -StdIn $encoded -Command (
        'AERIE_REHEARSAL=$(mktemp) && ' +
        'tr -dc ''A-Za-z0-9+/='' | base64 -d > $AERIE_REHEARSAL 2>/dev/null || ' +
        '{ echo ''the base64 rehearsal script did not decode on this node - it was truncated or mangled in transit'' >&2; exit 1; }; ' +
        'bash $AERIE_REHEARSAL; AERIE_RC=$?; rm -f $AERIE_REHEARSAL; exit $AERIE_RC')

    $out = "$($run.StdOut)"
    if ($out -notmatch '(?m)^--- END\s*$') {
        Write-Host ($run.StdErr) -ForegroundColor Red
        throw "The rehearsal script did not run to completion on $IPAddress (exit $($run.ExitCode)). The node may still be cordoned - this script's finally block uncordons it. Output above."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Checks'
    # ---------------------------------------------------------------- #

    $discovery = @{}
    foreach ($line in @((Get-ProbeSection -Output $out -Name 'DISCOVERY') -split "`n")) {
        if ($line -match '^(\w+)=(.*)$') { $discovery[$Matches[1]] = $Matches[2].Trim() }
    }

    $drainSection = Get-ProbeSection -Output $out -Name 'DRAIN'
    $marks = @{}
    foreach ($line in @($drainSection -split "`n")) {
        if ($line -match '^(cordon_at|drain_at|drain_exit|drain_done_at)=(.*)$') { $marks[$Matches[1]] = $Matches[2].Trim() }
    }

    function Get-Mark {
        param([string]$Name)
        if ($marks.ContainsKey($Name) -and $marks[$Name]) { return [double]$marks[$Name] }
        return $null
    }

    $cordonAt = Get-Mark 'cordon_at'
    $drainDoneAt = Get-Mark 'drain_done_at'
    $drainAt = Get-Mark 'drain_at'
    $drainExit = if ($marks.ContainsKey('drain_exit')) { [int]$marks['drain_exit'] } else { $null }

    # --- the headline: did the drain finish, and in how long ------------- #
    if ($null -ne $drainAt -and $null -ne $drainDoneAt) {
        $drainSeconds = [math]::Round($drainDoneAt - $drainAt, 1)
        if ($drainExit -eq 0) {
            Add-Check -Step '2.4.drain' -Name 'Drain completes' -Status 'Pass' -Detail "${drainSeconds}s, without --force"
        }
        else {
            Add-Check -Step '2.4.drain' -Name 'Drain completes' -Status 'Fail' -Detail "exit $drainExit after ${drainSeconds}s against a ${DrainTimeoutSeconds}s timeout. This is Phase 2's exit criterion and 4.1's deadline in one number - read the drain output below for what it blocked on"
        }
        if ($drainSeconds -le 90) {
            Add-Check -Step '2.4.drain' -Name 'Inside 4.1 budget' -Status 'Pass' -Detail "${drainSeconds}s of the 90s a personal-mode entry allows"
        }
        else {
            Add-Check -Step '2.4.drain' -Name 'Inside 4.1 budget' -Status 'Warn' -Detail "${drainSeconds}s, over the 90s 4.1 starts at. That deadline was a guess; this is the measurement that replaces it - raise it, or find what is slow"
        }
    }
    else {
        Add-Check -Step '2.4.drain' -Name 'Drain completes' -Status 'Fail' -Detail 'the remote script did not report its timestamps, so nothing here can be timed'
        $drainSeconds = $null
    }

    # --- continuity ------------------------------------------------------ #
    function Measure-Gap {
        param([string[]]$Lines, [scriptblock]$IsGood)
        $worst = 0.0; $failures = 0; $samples = 0; $gapStart = $null
        foreach ($line in $Lines) {
            $parts = @($line -split '\s+' | Where-Object { $_ })
            if ($parts.Count -lt 2) { continue }
            $samples++
            $t = [double]$parts[0]
            if (& $IsGood $parts[1]) {
                if ($null -ne $gapStart) { $worst = [math]::Max($worst, $t - $gapStart); $gapStart = $null }
            }
            else {
                $failures++
                if ($null -eq $gapStart) { $gapStart = $t }
            }
        }
        # A gap still open at the last sample is a gap that outlived the
        # rehearsal, which is worse than one that closed - counted, not
        # discarded.
        if ($null -ne $gapStart -and $Lines.Count -gt 0) {
            $lastParts = @($Lines[-1] -split '\s+' | Where-Object { $_ })
            if ($lastParts.Count -ge 1) { $worst = [math]::Max($worst, [double]$lastParts[0] - $gapStart) }
        }
        [pscustomobject]@{ WorstGapSeconds = [math]::Round($worst, 1); Failures = $failures; Samples = $samples }
    }

    $ingressLines = @((Get-ProbeSection -Output $out -Name 'INGRESS') -split "`n" | Where-Object { $_.Trim() })
    if ($discovery.ContainsKey('vip') -and $discovery['vip']) {
        $ing = Measure-Gap -Lines $ingressLines -IsGood { param($code) $code -match '^\d{3}$' -and $code -ne '000' }
        if ($ing.Samples -lt 10) {
            Add-Check -Step '2.4.ingress' -Name 'Ingress survives' -Status 'Fail' -Detail "only $($ing.Samples) probe sample(s) - too few to say anything, so this is unproven rather than clean"
        }
        elseif ($ing.Failures -eq 0) {
            Add-Check -Step '2.4.ingress' -Name 'Ingress survives' -Status 'Pass' -Detail "$($ing.Samples) probes to $($discovery['vip']):443, no gap"
        }
        else {
            Add-Check -Step '2.4.ingress' -Name 'Ingress survives' -Status 'Fail' -Detail "$($ing.Failures) of $($ing.Samples) probes failed, worst gap $($ing.WorstGapSeconds)s. Phase 2's exit says 'with no gap' - every host on the domain was off the internet for that long"
        }
    }
    else {
        Add-Check -Step '2.4.ingress' -Name 'Ingress survives' -Status 'Fail' -Detail 'no INGRESS_VIP in the aerie-cluster-config ConfigMap, so ingress continuity was never measured'
        $ing = $null
    }

    $dnsLines = @((Get-ProbeSection -Output $out -Name 'DNS') -split "`n" | Where-Object { $_.Trim() })
    $dnsTool = if ($discovery.ContainsKey('dnstool')) { $discovery['dnstool'] } else { 'none' }
    if ($dnsTool -eq 'none') {
        Add-Check -Step '2.4.dns' -Name 'DNS survives' -Status 'Fail' -Detail "no dig, nslookup or busybox on $IPAddress, so a DNS gap could not have been detected. Unproven is not clean - install dnsutils on a server node and re-run"
        $dns = $null
    }
    else {
        $dns = Measure-Gap -Lines $dnsLines -IsGood { param($r) $r -eq 'ok' }
        if ($dns.Samples -lt 10) {
            Add-Check -Step '2.4.dns' -Name 'DNS survives' -Status 'Fail' -Detail "only $($dns.Samples) probe sample(s) via $dnsTool - too few to say anything"
        }
        elseif ($dns.Failures -eq 0) {
            Add-Check -Step '2.4.dns' -Name 'DNS survives' -Status 'Pass' -Detail "$($dns.Samples) lookups via $dnsTool against $($discovery['dnsip']), no gap"
        }
        else {
            Add-Check -Step '2.4.dns' -Name 'DNS survives' -Status 'Fail' -Detail "$($dns.Failures) of $($dns.Samples) lookups failed, worst gap $($dns.WorstGapSeconds)s. In-cluster name resolution stopped for that long"
        }
    }

    # --- the two things 2.3 said to watch rather than trust --------------- #
    $timeline = @((Get-ProbeSection -Output $out -Name 'TIMELINE') -split "`n" | Where-Object { $_.Trim() })

    # Longhorn's per-node instance-manager PDB: present with 0 allowed on an
    # uncordoned node (the resting state), removed by Longhorn's node
    # controller once cordoned and it has satisfied itself no volume's last
    # healthy replica is at stake.
    $imLines = @($timeline | Where-Object { $_ -match '\spdb\slonghorn-system/instance-manager' })
    if ($imLines.Count -eq 0) {
        Add-Check -Step '2.4.longhorn' -Name 'Longhorn drops its PDB' -Status 'Warn' -Detail 'no longhorn-system instance-manager PDB was seen at all during the run, so there was nothing to watch drop'
    }
    elseif ($null -eq $cordonAt) {
        Add-Check -Step '2.4.longhorn' -Name 'Longhorn drops its PDB' -Status 'Fail' -Detail 'PDBs were observed but the cordon was not timestamped, so nothing can be timed against it'
    }
    else {
        # The one belonging to the drained node is the one that has to go. Its
        # name carries the node in a Longhorn-generated suffix, so instead of
        # parsing that, this watches for *any* instance-manager PDB count that
        # drops after the cordon - which on a three-node cluster is the same
        # observation and does not depend on Longhorn's naming.
        $before = @($imLines | Where-Object { [double](@($_ -split '\s+')[0]) -lt $cordonAt })
        $countBefore = if ($before.Count -gt 0) { @($before | ForEach-Object { (@($_ -split '\s+'))[2] } | Sort-Object -Unique).Count } else { 0 }
        $dropAt = $null
        foreach ($t in (@($imLines | ForEach-Object { [double](@($_ -split '\s+')[0]) } | Sort-Object -Unique))) {
            if ($t -le $cordonAt) { continue }
            $atT = @($imLines | Where-Object { [double](@($_ -split '\s+')[0]) -eq $t })
            if (@($atT | ForEach-Object { (@($_ -split '\s+'))[2] } | Sort-Object -Unique).Count -lt $countBefore) { $dropAt = $t; break }
        }
        if ($null -ne $dropAt) {
            Add-Check -Step '2.4.longhorn' -Name 'Longhorn drops its PDB' -Status 'Pass' -Detail "$([math]::Round($dropAt - $cordonAt, 1))s after the cordon, from $countBefore instance-manager PDB(s). This is the claim 2.3 said to watch rather than trust"
        }
        else {
            Add-Check -Step '2.4.longhorn' -Name 'Longhorn drops its PDB' -Status 'Warn' -Detail "$countBefore instance-manager PDB(s) throughout, none observed dropping. Either Longhorn had no replica on this node to protect, or the sampling missed it - the drain's own exit code is the load-bearing reading"
        }
    }

    # CNPG: the operator watches for the cordon and moves the primary off the
    # node ahead of the drain. Only meaningful if a primary was there.
    $cnpgLines = @($timeline | Where-Object { $_ -match '\scnpg\s' })
    $primaryHere = @($cnpgLines | Where-Object { $null -ne $cordonAt -and [double](@($_ -split '\s+')[0]) -lt $cordonAt -and (@($_ -split '\s+'))[3] -eq $NodeName })
    if ($cnpgLines.Count -eq 0) {
        Add-Check -Step '2.4.cnpg' -Name 'CNPG switchover' -Status 'Warn' -Detail 'no CNPG primary pods were visible, so there was no switchover to time'
    }
    elseif ($primaryHere.Count -eq 0) {
        Add-Check -Step '2.4.cnpg' -Name 'CNPG switchover' -Status 'Pass' -Detail "no primary was on '$NodeName', so no switchover was required. Re-run against the node that holds one to measure it - that is the case that dominates the budget"
    }
    else {
        $cluster = (@($primaryHere[0] -split '\s+'))[2]
        $moved = @($cnpgLines | Where-Object {
                [double](@($_ -split '\s+')[0]) -gt $cordonAt -and
                (@($_ -split '\s+'))[2] -eq $cluster -and
                (@($_ -split '\s+'))[3] -ne $NodeName
            } | Sort-Object { [double](@($_ -split '\s+')[0]) } | Select-Object -First 1)
        if ($moved) {
            $movedAt = [double](@($moved -split '\s+')[0])
            Add-Check -Step '2.4.cnpg' -Name 'CNPG switchover' -Status 'Pass' -Detail "$cluster moved off '$NodeName' $([math]::Round($movedAt - $cordonAt, 1))s after the cordon, to $((@($moved -split '\s+'))[3])"
        }
        else {
            Add-Check -Step '2.4.cnpg' -Name 'CNPG switchover' -Status 'Fail' -Detail "$cluster still reported '$NodeName' as its primary at the end of the run. The drain either blocked on its PDB or the operator did not act on the cordon"
        }
    }

    # --- the node came back ---------------------------------------------- #
    $after = Get-ProbeSection -Output $out -Name 'AFTER'
    $afterLine = @($after -split "`n" | Where-Object { $_ -match "^$([regex]::Escape($NodeName))\s" } | Select-Object -First 1)
    if ($afterLine -and $afterLine -notmatch 'SchedulingDisabled') {
        Add-Check -Step '2.4.restore' -Name 'Node uncordoned' -Status 'Pass' -Detail (($afterLine -split '\s+' | Where-Object { $_ })[1])
    }
    else {
        Add-Check -Step '2.4.restore' -Name 'Node uncordoned' -Status 'Fail' -Detail "'$NodeName' is still SchedulingDisabled after the run. Uncordon it: sudo k3s kubectl uncordon $NodeName"
    }

    Write-Host ''
    Write-Host 'The drain said:' -ForegroundColor DarkGray
    foreach ($line in @($drainSection -split "`n" | Where-Object { $_.Trim() -and $_ -notmatch '^(cordon_at|drain_at|drain_exit|drain_done_at)=' })) {
        Write-Host "  $line" -ForegroundColor DarkGray
    }
}
finally {
    # The safety net. The remote script uncordons on its own way out, but it
    # can be killed between the drain and that line - by a cancelled Actions
    # run, by a dropped SSH session - and a rehearsal that left a node
    # cordoned would be worse than not rehearsing.
    if ($ssh) {
        $u = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl uncordon $NodeName" -ConnectTimeoutSec 15
        if ($u.ExitCode -ne 0) {
            Write-Host ''
            Write-Host "COULD NOT UNCORDON '$NodeName' on the way out. Do it by hand:" -ForegroundColor Red
            Write-Host "  ssh $Username@$IPAddress 'sudo k3s kubectl uncordon $NodeName'" -ForegroundColor Red
        }
    }
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

# The three numbers 2.4 asks to be written down, in one place, so they can be
# copied into the plan rather than reconstructed from a table.
Write-Host ''
Write-Host 'For the plan (2.4 asks for these three):' -ForegroundColor Cyan
Write-Host "  drain, end to end          $(if ($null -ne $drainSeconds) { "${drainSeconds}s" } else { 'not measured' })"
Write-Host "  worst ingress gap          $(if ($ing) { "$($ing.WorstGapSeconds)s over $($ing.Samples) probes" } else { 'not measured' })"
Write-Host "  worst DNS gap              $(if ($dns) { "$($dns.WorstGapSeconds)s over $($dns.Samples) probes" } else { 'not measured' })"
foreach ($c in ($script:Checks | Where-Object { $_.Step -in '2.4.longhorn', '2.4.cnpg' })) {
    Write-Host "  $($c.Check.PadRight(26)) $($c.Detail)"
}

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
        "## Drain rehearsal - $NodeName - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, against $tick$IPAddress$tick in ${elapsed} min."
        ''
        '| Step | Check | Result | Detail |'
        '|---|---|---|---|'
    )
    foreach ($check in $script:Checks) {
        $mark = switch ($check.Result) { 'Pass' { 'PASS' } 'Warn' { 'WARN' } default { 'FAIL' } }
        $lines += "| $($check.Step) | $($check.Check) | $mark | $($check.Detail -replace '\|', '\|') |"
    }
    $lines += @(
        ''
        '### The three numbers docs/plans/part-time-node.md 2.4 asks for'
        ''
        "- drain, end to end: **$(if ($null -ne $drainSeconds) { "${drainSeconds}s" } else { 'not measured' })**"
        "- worst ingress gap: **$(if ($ing) { "$($ing.WorstGapSeconds)s" } else { 'not measured' })**"
        "- worst DNS gap: **$(if ($dns) { "$($dns.WorstGapSeconds)s" } else { 'not measured' })**"
        ''
        "_One cordon, one drain, one uncordon. The node is uncordoned on every exit path._"
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Drain rehearsal FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed} min." -ForegroundColor Red
    Write-Host "'$NodeName' was uncordoned on the way out. Phase 2 is not done until this passes - it is the dress rehearsal for every personal-mode entry."
    exit 1
}

Write-Host "Drain rehearsal passed: $($script:Checks.Count) check(s) in ${elapsed} min." -ForegroundColor Green
