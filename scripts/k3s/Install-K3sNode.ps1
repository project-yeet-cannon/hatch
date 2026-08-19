<#
.SYNOPSIS
    Installs and starts k3s server on one already-provisioned Aerie node,
    either initializing the cluster's embedded etcd or joining an existing
    one, over SSH from the Hyper-V host - no hand-typed SSH session.

.DESCRIPTION
    This is the entry point for the cluster plan Phase 2's first step. It is the
    same shape as Initialize-AerieNode.ps1 one phase up: it reuses
    ..\hyperv\lib\AerieSsh.ps1 to reach the node over SSH (the node itself was
    already built by that script in Phase 1), so the same key material and
    the same OpenSSH-client prerequisite apply here.

    Unlike Initialize-AerieNode.ps1 there is no VM to build - this only ever
    talks to a node that already answers SSH. It runs the official k3s
    install script (get.k3s.io) remotely, pinned to -K3sVersion, as either:

      -ClusterInit   server --cluster-init --disable servicelb --token <token>
      -JoinServer ip server --server https://<ip>:6443 --disable servicelb
                          --token <token>

    Exactly one of -ClusterInit / -JoinServer is required. Use -ClusterInit
    for the first server (forms the single-node etcd cluster); use
    -JoinServer <node1-ip> for every server after that.

    This is also how the cluster plan's Phase 6b.1 lands: re-dispatching
    against a node that is already active reconciles vm.max_map_count and
    etcd-expose-metrics without a full reinstall, restarting k3s only if the
    config file actually changed. Dispatch one node at a time and wait for
    every node to show Ready before reconfiguring the next - with two
    servers, quorum is 2 of 2 until Phase 7.

    Stages:
      1. Preflight - SSH key resolves, the OpenSSH client is present, the
                     node answers port 22, and (for -JoinServer) the target
                     server's apiserver (6443), etcd (2379-2380), and kubelet
                     (10250) ports answer too. Cheap failures before an
                     install that downloads a binary and starts etcd.
      2. Inspect   - checks whether k3s is already active on the node. If so,
                     the install is skipped (idempotent re-run) unless
                     -Reinstall forces a clean uninstall/reinstall.
      3. Node configuration - writes the vm.max_map_count sysctl drop-in
                     (applied live too) and /etc/rancher/k3s/config.yaml's
                     etcd-expose-metrics, both from the cluster plan Phase
                     6b.1. Idempotent, and restarts k3s only when it is
                     already active and the config file actually changed -
                     a fresh install below picks the file up on its own
                     first start.
      4. Install   - downloads and runs the pinned install script on the node
                     via sudo (the Phase 1 cloud-init user has
                     NOPASSWD:ALL sudo).
      5. Verify    - polls until the k3s.service is active and this node's
                     own name shows Ready in `k3s kubectl get nodes`, checks
                     vm.max_map_count and etcd's :2381 metrics endpoint, then
                     prints the full node list.

.PARAMETER K3sVersion
    Optional override. The pin normally comes from scripts/versions.json
    ('k3s.version'), which is committed so every node - and every rebuild of
    an old node - installs the same k3s. Pass this only for a one-off by-hand
    run; a real bump is a commit to that file. Never a latest/stable channel.
    See the cluster plan Phase 2 for why (same reproducibility reasoning as the
    Renovate ask under Goal 6.5).

.PARAMETER Token
    The shared cluster token, identical across every server in the cluster.
    Generate once with `openssl rand -hex 32` before installing node 1, and
    pass the same value again for every later node. Store it like the SSH
    keys - never in git.

.EXAMPLE
    # Node 1 - forms the cluster, at the committed pin
    .\Install-K3sNode.ps1 -VMName aerie-node-1 -IPAddress 10.0.0.21 `
        -Token $token -ClusterInit -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Node 2 - joins node 1
    .\Install-K3sNode.ps1 -VMName aerie-node-2 -IPAddress 10.0.0.22 `
        -Token $token -JoinServer 10.0.0.21 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Trying a bump by hand before committing it
    .\Install-K3sNode.ps1 -VMName aerie-node-3 -IPAddress 10.0.0.23 `
        -K3sVersion v1.36.3+k3s1 -Token $token -JoinServer 10.0.0.21 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$')]
    [string]$VMName,

    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    # Defaults to the committed pin in scripts/versions.json - see the
    # .PARAMETER note above before passing this explicitly.
    [ValidatePattern('^v\d+\.\d+\.\d+(\+k3s\d+)?$')]
    [string]$K3sVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9]{16,}$')]
    [string]$Token,

    [Parameter(Mandatory, ParameterSetName = 'ClusterInit')]
    [switch]$ClusterInit,

    [Parameter(Mandatory, ParameterSetName = 'Join')]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$JoinServer,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [int]$ReadyTimeoutMinutes = 10,

    [switch]$PreflightOnly,

    # k3s is already active on this node (e.g. re-running after a partial
    # failure, or bumping -K3sVersion/-Token): normally that's treated as
    # already-done and Install is skipped. This instead runs the generated
    # k3s-uninstall.sh first, so the node comes up clean under today's
    # inputs. Etcd members don't remove themselves on uninstall - if this is
    # a server that isn't the last one standing, remove it from the cluster's
    # member list first (`k3s kubectl` from a surviving node) or it leaves a
    # dead voter behind.
    [switch]$Reinstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')
. (Join-Path $PSScriptRoot '..\lib\AerieVersions.ps1')

# Resolved here rather than as a param default: param() has to be the first
# statement in the file, so the manifest reader isn't loaded yet at that point.
if (-not $K3sVersion) {
    $K3sVersion = Get-AerieVersion -Name 'k3s.version' -Pattern '^v\d+\.\d+\.\d+\+k3s\d+$'
    $script:K3sVersionSource = 'scripts/versions.json'
}
else {
    $script:K3sVersionSource = '-K3sVersion override'
}

$script:StageNumber = 0
function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Get-ProbeSection {
    <#
    .SYNOPSIS
        Pulls one '--- name' section out of combined probe output - the same
        marker shape Initialize-NodeStorage.ps1 uses, for the same reason: one
        SSH round trip for everything a stage reasons about, rather than one
        per fact.
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

function ConvertFrom-RemoteFileProbe {
    <#
    .SYNOPSIS
        Decodes a probed file's base64, or returns $null for the 'NONE'
        sentinel the probe emits when the file does not exist yet.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Encoded)
    if ([string]::IsNullOrWhiteSpace($Encoded) -or $Encoded -eq 'NONE') { return $null }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Encoded))
}

# Phase 6b.1's two node-level settings. Fixed, not parameters: both values are
# structural - OpenSearch's bootstrap check and k3s's own default are what set
# them, not this installation's preference - so a knob here would just be a
# second place either could drift from the plan.
$sysctlDropInPath = '/etc/sysctl.d/60-aerie-opensearch.conf'
$desiredSysctlFile = @(
    '# Managed by Aerie: scripts/k3s/Install-K3sNode.ps1 (the cluster plan Phase 6b.1).'
    '# OpenSearch (6b.9) refuses to start below this - the Linux default is 65530.'
    'vm.max_map_count=262144'
    ''
) -join "`n"

$k3sConfigPath = '/etc/rancher/k3s/config.yaml'
$desiredK3sConfig = @(
    '# Managed by Aerie: scripts/k3s/Install-K3sNode.ps1 (the cluster plan Phase 6b.1).'
    '# k3s defaults this to false, leaving etcd metrics on :2381 unreachable - the'
    '# quorum alert in 6b.8 has nothing to evaluate without it.'
    'etcd-expose-metrics: true'
    ''
) -join "`n"

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$hostname = $VMName.ToLowerInvariant()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
        $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey. This is the same key Initialize-AerieNode.ps1 baked into the node in Phase 1.')
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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName $VMName
            $privateKeyPath = $resolvedKey.Path
            $tempKeyFile = $resolvedKey.TempFile
            $keyFingerprint = $resolvedKey.Fingerprint
        }
        catch {
            $failures.Add($_.Exception.Message)
        }
    }

    # A cheap signal that this is even the right IP before an install that
    # downloads a binary and starts etcd - the same "fail before the
    # expensive part" reasoning Initialize-AerieNode.ps1 uses for its own
    # checks.
    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        $failures.Add("$IPAddress isn't answering on port 22. Confirm the node from Phase 1 is up and -IPAddress is right.")
    }

    if ($JoinServer) {
        # Only proves these ports answer from this machine, not from the
        # joining node itself - but a miss here is almost always a typo'd
        # -JoinServer or a firewalled node, so it's worth catching before the
        # remote install even starts. The cluster plan Phase 2 calls out
        # 6443/2379-2380/10250/8472 as the ports node-to-node traffic needs;
        # UDP 8472 (flannel VXLAN) is deliberately not probed here - a TCP
        # connect can't meaningfully test a connectionless port, and
        # Debian/Ubuntu cloud images ship with no firewall active by default,
        # which is why this has stayed a non-issue in practice.
        $joinPorts = [ordered]@{
            6443  = 'k3s apiserver'
            2379  = 'etcd client'
            2380  = 'etcd peer'
            10250 = 'kubelet'
        }
        foreach ($port in $joinPorts.Keys) {
            if (-not (Test-TcpPort -IPAddress $JoinServer -Port $port)) {
                $failures.Add("-JoinServer $JoinServer isn't answering on $port ($($joinPorts[$port])) from this machine. Confirm node 1 finished -ClusterInit, the address is right, and nothing is firewalling node-to-node traffic.")
            }
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed for '$VMName' ($IPAddress) with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Node:      $VMName ($IPAddress)"
    Write-Host "Role:      $(if ($ClusterInit) { 'cluster-init (first server, forms etcd)' } else { "join existing cluster at $JoinServer" })"
    Write-Host "k3s:       $K3sVersion (pinned, from $script:K3sVersionSource)"
    if ($keyFingerprint) {
        Write-Host "SSH key:   $keyFingerprint"
    }
    Write-Host 'Preflight OK.'

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here without touching the node.' -ForegroundColor Yellow
        return
    }

    $knownHostsFile = Join-Path $env:TEMP "aerie-k3s-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Inspect existing state'
    # ---------------------------------------------------------------- #

    $probeScript = (@(
            'echo ''--- service'''
            'systemctl is-active k3s 2>/dev/null || echo inactive'
            'echo ''--- version'''
            '(command -v k3s >/dev/null 2>&1 && k3s --version 2>/dev/null | head -n1) || echo ''k3s not installed'''
            'echo ''--- sysctl'''
            'sysctl -n vm.max_map_count 2>/dev/null || echo 0'
            'echo ''--- sysctl-file'''
            'test -f {0} && { base64 -w0 {0}; echo; } || echo NONE'
            'echo ''--- k3s-config'''
            'test -f {1} && { base64 -w0 {1}; echo; } || echo NONE'
            'echo ''--- end'''
        ) -join '; ') -f $sysctlDropInPath, $k3sConfigPath

    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 15
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdErr)"
    }

    $serviceState = (Get-ProbeSection -Output $probe.StdOut -Name 'service').Trim()
    if (-not $serviceState) { $serviceState = 'unknown' }
    $installedVersion = (Get-ProbeSection -Output $probe.StdOut -Name 'version').Trim()
    if (-not $installedVersion) { $installedVersion = 'unknown' }
    $liveMaxMapCount = (Get-ProbeSection -Output $probe.StdOut -Name 'sysctl').Trim()
    $currentSysctlFile = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'sysctl-file')
    $currentK3sConfig = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'k3s-config')

    Write-Host "k3s.service: $serviceState"
    Write-Host "k3s binary:  $installedVersion"

    $alreadyActive = $serviceState -eq 'active'
    # Captured before -Reinstall can flip $alreadyActive below - it gates the
    # Phase 2 checklist at the very end, which makes no sense to print for a
    # run that only reconciled Phase 6b.1's node settings on an already-active
    # node and never touched the install pipeline at all.
    $wasAlreadyActive = $alreadyActive
    if ($alreadyActive -and $Reinstall) {
        Write-Warning "-Reinstall: k3s is active on '$VMName' - running its uninstall script before reinstalling with today's inputs. If this is a server node and other servers are still up, it will not have removed itself from the etcd member list first."
        $uninstall = Invoke-NodeSsh @ssh -Command "test -x /usr/local/bin/k3s-uninstall.sh && sudo /usr/local/bin/k3s-uninstall.sh || echo 'no k3s-uninstall.sh found'" -ConnectTimeoutSec 60
        if ($uninstall.ExitCode -ne 0) {
            throw "k3s-uninstall.sh failed on $IPAddress (exit $($uninstall.ExitCode)):`n$($uninstall.StdOut)$($uninstall.StdErr)"
        }
        Write-Host '  uninstalled.'
        $alreadyActive = $false
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Node configuration'
    # ---------------------------------------------------------------- #
    #
    # The cluster plan Phase 6b.1's two node-level settings, applied here so
    # they exist before k3s ever starts on a fresh node - see the doc for why
    # neither can be a chart-side or in-cluster fix. Idempotent: a re-run
    # against a node that already has both settings reports nothing changed.

    $configActions = New-Object Collections.Generic.List[string]

    if ($currentSysctlFile -eq $desiredSysctlFile) {
        Write-Host "$sysctlDropInPath already matches - not rewriting."
    }
    else {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($desiredSysctlFile))
        $writeSysctlFile = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            'sudo mkdir -p /etc/sysctl.d && echo {0} | base64 -d | sudo tee {1} >/dev/null' -f $encoded, $sysctlDropInPath
        )
        if ($writeSysctlFile.ExitCode -ne 0) {
            throw "Writing $sysctlDropInPath on $IPAddress failed (exit $($writeSysctlFile.ExitCode)):`n$($writeSysctlFile.StdOut)$($writeSysctlFile.StdErr)"
        }
        $configActions.Add("wrote $sysctlDropInPath")
        Write-Host "$sysctlDropInPath written."
    }

    if ($liveMaxMapCount -eq '262144') {
        Write-Host 'vm.max_map_count: already 262144 live.'
    }
    else {
        $applySysctl = Invoke-NodeSsh @ssh -Command 'sudo sysctl -w vm.max_map_count=262144' -ConnectTimeoutSec 15
        if ($applySysctl.ExitCode -ne 0) {
            throw "sudo sysctl -w vm.max_map_count=262144 failed on $IPAddress (exit $($applySysctl.ExitCode)):`n$($applySysctl.StdOut)$($applySysctl.StdErr)"
        }
        $configActions.Add("applied vm.max_map_count=262144 live (was $liveMaxMapCount)")
        Write-Host "vm.max_map_count: applied live (was $liveMaxMapCount)."
    }

    $k3sConfigChanged = $currentK3sConfig -ne $desiredK3sConfig
    if (-not $k3sConfigChanged) {
        Write-Host "$k3sConfigPath already matches - not rewriting."
    }
    else {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($desiredK3sConfig))
        $writeK3sConfig = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            'sudo mkdir -p /etc/rancher/k3s && echo {0} | base64 -d | sudo tee {1} >/dev/null' -f $encoded, $k3sConfigPath
        )
        if ($writeK3sConfig.ExitCode -ne 0) {
            throw "Writing $k3sConfigPath on $IPAddress failed (exit $($writeK3sConfig.ExitCode)):`n$($writeK3sConfig.StdOut)$($writeK3sConfig.StdErr)"
        }
        $configActions.Add("wrote $k3sConfigPath")
        Write-Host "$k3sConfigPath written."
    }

    if ($alreadyActive -and $k3sConfigChanged) {
        # k3s only reads config.yaml at start, so a value change on a node
        # that is already running does nothing until the service comes back -
        # a restart, not the heavier -Reinstall (which also churns etcd
        # membership, per its own notes above). A fresh install below picks
        # the file up on its own first start, so this only fires when
        # reconfiguring a node that joined in an earlier phase.
        Write-Warning "Restarting k3s on '$VMName' to apply etcd-expose-metrics. With two servers, quorum is 2 of 2 until Phase 7 - wait for every node to show Ready (kubectl get nodes) before reconfiguring the next one."
        $restart = Invoke-NodeSsh @ssh -Command 'sudo systemctl restart k3s' -ConnectTimeoutSec 30
        if ($restart.ExitCode -ne 0) {
            throw "systemctl restart k3s failed on $IPAddress (exit $($restart.ExitCode)):`n$($restart.StdOut)$($restart.StdErr)"
        }
        $configActions.Add('restarted k3s to apply etcd-expose-metrics')
    }

    if ($configActions.Count -eq 0) {
        Write-Host 'Node configuration already matched Phase 6b.1 - nothing changed.'
    }

    if ($alreadyActive) {
        Write-Host "k3s is already active on '$VMName' - skipping install (pass -Reinstall to force a clean reinstall). Proceeding to Verify."
    }
    else {
        # ---------------------------------------------------------------- #
        Write-Stage 'Install'
        # ---------------------------------------------------------------- #

        $k3sArgs = if ($ClusterInit) {
            @('server', '--cluster-init', '--disable', 'servicelb', '--token', $Token)
        }
        else {
            @('server', '--server', "https://${JoinServer}:6443", '--disable', 'servicelb', '--token', $Token)
        }

        # Downloaded to a file rather than piped straight into sh: leaves the
        # install script on the node for post-mortem, and keeps the token out
        # of a `curl | sh` one-liner that would otherwise need careful
        # quoting to survive PowerShell -> ssh -> remote-shell three times.
        #
        # `sudo env VAR=val cmd`, not `sudo VAR=val cmd`: a bare prefix
        # assignment sets the variable for sudo's own invocation, and most
        # sudoers configs reset the environment before exec'ing the target
        # command, silently dropping it unless it's allow-listed. Routing it
        # through `env` sets it directly on the process sudo execs, which
        # works regardless of that policy.
        $installCmd = "curl -sfL https://get.k3s.io -o /tmp/k3s-install.sh && sudo env INSTALL_K3S_VERSION='$K3sVersion' sh /tmp/k3s-install.sh $($k3sArgs -join ' ')"

        Write-Host "Installing k3s $K3sVersion on '$VMName' ..."
        $install = Invoke-NodeSsh @ssh -Command $installCmd -ConnectTimeoutSec 30
        if ($install.ExitCode -ne 0) {
            throw "k3s install failed on $IPAddress (exit $($install.ExitCode)):`n$($install.StdOut)$($install.StdErr)"
        }
        Write-Host '  install script finished.'
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    $deadline = (Get-Date).AddMinutes($ReadyTimeoutMinutes)

    Write-Host 'Waiting for k3s.service to be active ...'
    while ($true) {
        $status = Invoke-NodeSsh @ssh -Command 'systemctl is-active k3s 2>/dev/null || echo inactive' -ConnectTimeoutSec 15
        if ($status.ExitCode -eq 0 -and $status.StdOut.Trim() -eq 'active') { break }
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $ReadyTimeoutMinutes min waiting for k3s.service to become active on $IPAddress. Inspect with: ssh $Username@$IPAddress sudo journalctl -u k3s -n 100"
        }
        Start-Sleep -Seconds 5
    }
    Write-Host '  k3s.service active.'

    Write-Host "Waiting for node '$hostname' to report Ready ..."
    while ($true) {
        $nodeStatus = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl get node $hostname --no-headers 2>/dev/null" -ConnectTimeoutSec 15
        if ($nodeStatus.ExitCode -eq 0 -and $nodeStatus.StdOut -match '^\S+\s+Ready\b') { break }
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $ReadyTimeoutMinutes min waiting for '$hostname' to report Ready. Inspect with: ssh $Username@$IPAddress sudo k3s kubectl get nodes -o wide"
        }
        Start-Sleep -Seconds 5
    }
    Write-Host '  node Ready.'

    # Phase 6b.1's own exit criteria - checked here rather than left to the
    # Phase 6 gate script, so a run that reports success actually satisfies
    # them instead of finding out at 6b.5/6b.9, several steps and possibly
    # days later.
    Write-Host 'Verifying Phase 6b.1 node settings ...'
    $maxMapCheck = Invoke-NodeSsh @ssh -Command 'sysctl -n vm.max_map_count' -ConnectTimeoutSec 15
    if ($maxMapCheck.ExitCode -ne 0 -or $maxMapCheck.StdOut.Trim() -ne '262144') {
        throw "vm.max_map_count on $IPAddress reads '$($maxMapCheck.StdOut.Trim())', not 262144. OpenSearch (6b.9) will refuse to start until this is fixed."
    }
    Write-Host '  vm.max_map_count: 262144.'

    # etcd binds :2381 as part of the same k3s server process whose apiserver
    # side just reported this node Ready - a short-lived race, not a
    # misconfiguration, so this gets its own small retry window rather than
    # failing on the first miss the instant k3s.service/Ready is achieved.
    $etcdDeadline = (Get-Date).AddSeconds(60)
    $etcdMetricsCheck = $null
    while ($true) {
        $etcdMetricsCheck = Invoke-NodeSsh @ssh -Command 'curl -s --max-time 5 http://127.0.0.1:2381/metrics | head -1' -ConnectTimeoutSec 15
        if ($etcdMetricsCheck.ExitCode -eq 0 -and $etcdMetricsCheck.StdOut.Trim()) { break }
        if ((Get-Date) -gt $etcdDeadline) {
            throw "etcd's metrics endpoint (127.0.0.1:2381) is not answering on $IPAddress after 60s. Confirm $k3sConfigPath has etcd-expose-metrics: true and that k3s restarted cleanly: ssh $Username@$IPAddress sudo journalctl -u k3s -n 100"
        }
        Start-Sleep -Seconds 5
    }
    Write-Host '  etcd metrics endpoint (:2381): answering.'

    $nodeList = Invoke-NodeSsh @ssh -Command 'sudo k3s kubectl get nodes -o wide' -ConnectTimeoutSec 15
    Write-Host ''
    Write-Host $nodeList.StdOut.TrimEnd()

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "'$VMName' is running k3s $K3sVersion in ${elapsed} min." -ForegroundColor Green
    if (-not $ClusterInit) {
        Write-Warning 'Two-node embedded etcd has worse availability than one node (tolerates zero losses, not one) - the cluster plan flags this build window as non-production until the third server rejoins in Phase 7.'
    }
    if ($configActions.Count -eq 0) {
        Write-Host 'Phase 6b.1 node settings: already matched - nothing changed.'
    }
    else {
        Write-Host 'Phase 6b.1 node settings changed:'
        foreach ($action in $configActions) { Write-Host "  - $action" }
    }

    if (-not $wasAlreadyActive) {
        Write-Host ''
        Write-Host 'Phase 2 checklist - confirm and tick off in docs/plans/swarm/phase-2-k3s-flux-secrets.md:'
        Write-Host "  - this node installed and Ready                 (verified above)"
        Write-Host '  - cluster token generated once, stored like the SSH keys, never in git'
        Write-Host '  - node-to-node TCP ports open (6443/2379-2380/10250)   (verified above for -JoinServer runs; UDP 8472/flannel is not checked)'
        Write-Host '  - Provision 2: seed secrets  (scripts/secrets/, once both nodes are up)'
        Write-Host '  - Provision 3: bootstrap Flux (scripts/flux/)'
    }

    # A no-op outside Actions, which is the point: manual invocation is a
    # last resort here (see scripts/hyperv/README.md) and provision-1-install
    # -k3s.yml is the primary way this runs - its job summary is the audit
    # record of which node joined with which version, so it carries the same
    # detail Initialize-AerieNode.ps1 writes for Phase 1.
    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $lines = @(
            "## k3s installed on $tick$VMName$tick"
            ''
            '| | |'
            '|---|---|'
            "| Role | $(if ($ClusterInit) { 'cluster-init (forms etcd)' } else { "join $tick$JoinServer$tick" }) |"
            "| Address | $tick$IPAddress$tick |"
            "| k3s version | $tick$K3sVersion$tick ($script:K3sVersionSource) |"
            "| Reinstalled | $Reinstall |"
            "| Elapsed | ${elapsed} min |"
            ''
            "**Phase 6b.1 node settings:** $(if ($configActions.Count -eq 0) { 'already matched - nothing changed' } else { ($configActions -join '; ') })"
            ''
            '<details><summary>kubectl get nodes -o wide</summary>'
            ''
            $fence
            $nodeList.StdOut.TrimEnd()
            $fence
            ''
            '</details>'
        )
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
}
