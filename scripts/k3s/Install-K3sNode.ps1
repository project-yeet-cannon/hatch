<#
.SYNOPSIS
    Installs and starts k3s on one already-provisioned Aerie node - as a
    server that initializes the cluster's embedded etcd or joins an existing
    one, or as an agent (worker) that joins without ever becoming an etcd
    member - over SSH from the Hyper-V host, no hand-typed SSH session.

.DESCRIPTION
    This is the entry point for the cluster plan Phase 2's first step. It is the
    same shape as Initialize-AerieNode.ps1 one phase up: it reuses
    ..\hyperv\lib\AerieSsh.ps1 to reach the node over SSH (the node itself was
    already built by that script in Phase 1), so the same key material and
    the same OpenSSH-client prerequisite apply here.

    Unlike Initialize-AerieNode.ps1 there is no VM to build - this only ever
    talks to a node that already answers SSH. It runs the official k3s
    install script (get.k3s.io) remotely, pinned to -K3sVersion, as one of:

      -ClusterInit          server --cluster-init --disable servicelb
                                   --token <token>
      -JoinServer ip        server --server https://<ip>:6443
                                   --disable servicelb --token <token>
      -JoinServer ip -Agent agent  --server https://<ip>:6443 --token <token>

    Exactly one of -ClusterInit / -JoinServer is required. Use -ClusterInit
    for the first server (forms the single-node etcd cluster); use
    -JoinServer <node1-ip> for every server after that; add -Agent to join as
    a worker instead.

    An agent is a normal, schedulable node that runs no control plane - no
    apiserver, no etcd member, '<none>' under ROLES - which is the right shape
    for capacity that is expected to come and go (the part-time-node plan
    Phase 1). It changes four things here, and each of them is a way to get an
    install that looks clean and is wrong:

      - the unit is k3s-agent.service, and the install script writes
        k3s-agent-uninstall.sh rather than k3s-uninstall.sh beside it.
      - /etc/rancher/k3s/config.yaml is read by 'k3s agent' too, but k3s
        filters the keys it finds there only against the *server* command's
        flags (pkg/configfilearg's ValidFlags has no agent entry, and its
        stripInvalidFlags returns the list untouched when a command has none).
        etcd-expose-metrics therefore reaches the agent CLI verbatim and the
        unit dies at start on 'flag provided but not defined'. An agent gets
        no config.yaml at all - and one left behind by an earlier server
        install is removed here rather than inherited.
      - an agent has no kubeconfig and no apiserver, so every cluster-level
        question in Verify is asked of -JoinServer over SSH instead. That is
        why port 22 there is a preflight check for agents.
      - there is no etcd, so the :2381 metrics probe is skipped.

    This is also how the cluster plan's Phase 6b.1 and the node-storage
    plan's Phase 2 land: re-dispatching against a node that is already active
    reconciles all four node-level settings - vm.max_map_count,
    etcd-expose-metrics, kubelet's image-GC thresholds and journald's size cap
    - without a full reinstall, restarting k3s only if a file k3s reads at
    start actually changed (an agent reconciles three of the four, and its
    restart costs the cluster nothing but the node). Dispatch one node at a
    time and wait for every node to show Ready before reconfiguring the next:
    each server restart takes an etcd member down for its duration.

    Stages:
      1. Preflight - SSH key resolves, the OpenSSH client is present, the
                     node answers port 22, and (for -JoinServer) the target
                     server's apiserver (6443) and kubelet (10250) ports
                     answer too - plus etcd (2379-2380) when joining as a
                     server, which an agent never speaks, and port 22 when
                     joining as an agent, which Verify needs. Cheap failures
                     before an install that downloads a binary and starts
                     etcd.
      2. Inspect   - checks whether k3s is already active on the node. If so,
                     the install is skipped (idempotent re-run) unless
                     -Reinstall forces a clean uninstall/reinstall.
      3. Node configuration - writes four node-level settings (three on an
                     agent, which has no config.yaml): the
                     vm.max_map_count sysctl drop-in (applied live too) and
                     /etc/rancher/k3s/config.yaml's etcd-expose-metrics from
                     the cluster plan Phase 6b.1, plus kubelet's image-GC
                     thresholds at 70/55 and journald's 512M cap from the
                     node-storage plan Phase 2. Idempotent, and restarts k3s
                     only when it is already active and a file k3s reads at
                     start actually changed - a fresh install below picks
                     both up on its own first start; journald is restarted
                     and vacuumed in place instead, since k3s does not read
                     it. On servers only, also symlinks /root/.kube/config to
                     k3s's own kubeconfig, so kubectl and flux both work for
                     root with no KUBECONFIG to remember while debugging from
                     the node's own console - operator ergonomics, not a
                     cluster plan step, and safe as a dangling link on a
                     node that hasn't been installed yet. An agent never gets
                     that file, so the link would dangle forever.
      4. Install   - downloads and runs the pinned install script on the node
                     via sudo (the Phase 1 cloud-init user has
                     NOPASSWD:ALL sudo).
      5. Verify    - polls until the unit is active and this node's own name
                     shows Ready in `k3s kubectl get nodes` (asked of the node
                     itself for a server, of -JoinServer for an agent), checks
                     vm.max_map_count, etcd's :2381 metrics endpoint (servers
                     only) and the image-GC thresholds the running kubelet
                     actually resolved (not the file it was handed), asserts
                     ROLES is '<none>' on an agent, reconciles -NodeLabel
                     against the live Node object, reports journald's size
                     against its cap, then prints the full node list.

.PARAMETER K3sVersion
    Optional override. The pin normally comes from scripts/versions.json
    ('k3s.version'), which is committed so every node - and every rebuild of
    an old node - installs the same k3s. Pass this only for a one-off by-hand
    run; a real bump is a commit to that file. Never a latest/stable channel.
    See the cluster plan Phase 2 for why (same reproducibility reasoning as the
    Renovate ask under Goal 6.5).

.PARAMETER Token
    The shared cluster token, identical across every node in the cluster,
    agents included. Generate once with `openssl rand -hex 32` before
    installing node 1, and pass the same value again for every later node.
    Store it like the SSH keys - never in git.

.PARAMETER Agent
    Join as a worker (`k3s agent`) rather than as a fourth server. Requires
    -JoinServer. See the .DESCRIPTION above for the four things it changes,
    and the part-time-node plan for why a cluster wants the option at all.

.PARAMETER NodeLabel
    Zero or more `key=value` labels to put on this node, passed to the install
    as --node-label and reconciled against the live Node object on every
    re-run. Placement rules should key off these rather than off a node
    *name*: a name says which machine it is, a label says what it can do, and
    only the second survives the machine being replaced.

    Labels named here are added or overwritten; nothing is removed, since this
    script does not own the whole label set (kubelet, k3s and Longhorn all put
    their own there). --node-label alone would not be enough: kubelet applies
    it at registration and never again, so a re-dispatch against a node that
    already joined would report success having changed nothing.

    Use a prefix of your own (aerie.family/...). Most kubernetes.io/ and
    k8s.io/ keys are refused to a self-registering kubelet by the
    NodeRestriction admission plugin, which fails the node's *registration* -
    an install that ends at 'timed out waiting for Ready' with the reason
    only in the node's journal.

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
    # A worker, not a fourth control-plane member - the part-time-node plan's
    # host D, labelled by what it is rather than by what it is called
    .\Install-K3sNode.ps1 -VMName aerie-node-3 -IPAddress 10.0.0.24 `
        -Token $token -JoinServer 10.0.0.21 -Agent `
        -NodeLabel 'aerie.family/availability=part-time', 'aerie.family/storage=none' `
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
    [Parameter(Mandatory, ParameterSetName = 'Agent')]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$JoinServer,

    # Only valid alongside -JoinServer: an agent has nothing to initialize.
    # PowerShell resolves this on its own - -JoinServer alone satisfies the
    # 'Join' set, -JoinServer with -Agent satisfies only 'Agent'.
    [Parameter(Mandatory, ParameterSetName = 'Agent')]
    [switch]$Agent,

    # key=value, validated to Kubernetes' own label grammar (optional
    # DNS-subdomain prefix, then a <=63 character name and value). Rejecting a
    # malformed one here rather than at the apiserver keeps a typo from
    # becoming an install that succeeds and a placement rule that never
    # matches - and keeps the value shell-safe on its way through
    # sudo -> install script -> systemd unit.
    # (?-i) because ValidatePattern matches case-insensitively by default,
    # which would let an upper-case prefix through - and Kubernetes requires
    # that half to be a lower-case DNS subdomain.
    [ValidatePattern('(?-i)^([a-z0-9]([-a-z0-9.]*[a-z0-9])?/)?[A-Za-z0-9]([-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?=([A-Za-z0-9]([-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?)?$')]
    [string[]]$NodeLabel = @(),

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
    # dead voter behind. An agent has no member list to leave, so -Reinstall
    # there costs only the pods it was running. It is not how a node changes
    # role: Inspect refuses a dispatch that finds the other unit active, and
    # names the uninstall script to run first.
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

# One switch, read in a dozen places below - named once so no stage has to
# re-derive it from the parameter set. k3s's install script names the unit
# after the command it was given ('k3s' for a server, 'k3s-<command>'
# otherwise) and writes the matching uninstall script beside it, so the
# service name is a role fact rather than a constant.
$isAgent = [bool]$Agent
$serviceName = if ($isAgent) { 'k3s-agent' } else { 'k3s' }
$otherServiceName = if ($isAgent) { 'k3s' } else { 'k3s-agent' }
$uninstallScript = "/usr/local/bin/$serviceName-uninstall.sh"
$roleDescription =
if ($ClusterInit) { 'cluster-init (first server, forms etcd)' }
elseif ($isAgent) { "agent (worker) joining $JoinServer - no etcd, no apiserver" }
else { "server joining $JoinServer" }

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

# The node's managed settings - two from the cluster plan Phase 6b.1, two from
# the node-storage plan Phase 2. Fixed, not parameters: every value here is
# structural - OpenSearch's bootstrap check, k3s's own default, a ratio against
# the disk size Move-NodeOsDisk.ps1 gives a node - not this installation's
# preference, so a knob would just be a second place any of them could drift
# from its plan.
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

# The node-storage plan's 2.1, and the one place this script departs from what
# that plan sketched. It asked for `kubelet-arg: image-gc-high-threshold=70` in
# config.yaml above; --image-gc-high-threshold and its low twin have been
# deprecated kubelet flags since 1.15 ("set this via the config file"), and
# k3s already runs kubelet with --config-dir pointed at the directory below -
# so the drop-in is the supported spelling of the same setting, on a mechanism
# these nodes are demonstrably already using. Same restart cost either way:
# kubelet reads both at start and neither is live-reloadable.
#
# k3s writes 00-k3s-defaults.conf into this directory on every start and leaves
# anything else alone - its own documentation calls a drop-in placed here the
# recommended way to change a kubelet default. Kubelet merges them in lexical
# order, and the 50- prefix is chosen to land after every name k3s reserves for
# itself: 00- for those defaults, and 10-cli-config.conf / 20-cli-config-dir/
# for the copies it makes of a kubelet config passed on the command line. The
# directory is k3s's, but this file is not - k3s-uninstall.sh takes the whole
# tree with it, which is why -Reinstall runs before this stage rather than
# after.
$kubeletDropInDir = '/var/lib/rancher/k3s/agent/etc/kubelet.conf.d'
$kubeletDropInPath = "$kubeletDropInDir/50-aerie-image-gc.conf"
# Named once and used three times - written into the file, and asserted against
# the running kubelet in Verify. The percentages are the whole of 2.1, so they
# should not be able to disagree with themselves.
$imageGcHighTarget = 70
$imageGcLowTarget = 55
$desiredKubeletDropIn = @(
    '# Managed by Aerie: scripts/k3s/Install-K3sNode.ps1 (the node-storage plan 2.1).'
    "# kubelet's defaults are 85/80, so the first image GC of a node's life runs at"
    '# the same threshold that gates eviction - housekeeping arriving as pressure.'
    "# $imageGcHighTarget/$imageGcLowTarget against the 100 GB OS disk trims at 70 GB, twice the ~32 GB"
    '# steady state, and leaves the eviction threshold something to be a'
    '# threshold for.'
    'apiVersion: kubelet.config.k8s.io/v1beta1'
    'kind: KubeletConfiguration'
    "imageGCHighThresholdPercent: $imageGcHighTarget"
    "imageGCLowThresholdPercent: $imageGcLowTarget"
    ''
) -join "`n"

# The node-storage plan's 2.2. Written here for the three nodes that already
# exist and by cloud-init for every node built after - byte-identical in both
# places on purpose, so a new node's first Provision 1 run reports this as
# already matching rather than rewriting a file it agrees with. Change one and
# change the other: scripts/hyperv/cloud-init/user-data.tmpl.yaml.
$journaldDropInPath = '/etc/systemd/journald.conf.d/60-aerie-journal-cap.conf'
# One number, three uses: the file, the vacuum that makes an existing node
# match it, and the size Verify reports against. systemd's M is a MiB, which
# is also what PowerShell's 1MB is, so the two agree without a conversion.
$journaldCapMiB = 512
$journaldCap = "${journaldCapMiB}M"
$desiredJournaldDropIn = @(
    '# Managed by Aerie: scripts/k3s/Install-K3sNode.ps1 and'
    '# scripts/hyperv/cloud-init/user-data.tmpl.yaml (the node-storage plan 2.2).'
    "# journald's default ceiling is 10% of the filesystem capped at 4G, so the"
    '# 100 GB OS disk raised it rather than bounding it. This is a fixed ceiling'
    '# instead: enough journal to debug from, and it cannot grow into the disk.'
    '[Journal]'
    "SystemMaxUse=$journaldCap"
    ''
) -join "`n"

# Operator ergonomics, not a cluster plan step: k3s writes its own kubeconfig
# here on every server, but nothing points root at it, so a bare `kubectl` or
# `flux` after `sudo -i` fails with a connection-refused rather than a
# missing-config error - it just talks to the default localhost:8080 instead.
# `k3s kubectl` (used everywhere else in scripts/) doesn't need this; plain
# kubectl and flux both do, and 6b.4's debugging-while-building-the-stack is
# exactly when an operator reaches for them on the node's own console.
$kubeconfigLinkDir = '/root/.kube'
$kubeconfigLinkPath = '/root/.kube/config'
$k3sYamlPath = '/etc/rancher/k3s/k3s.yaml'

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
        #
        # A list of pairs rather than a hashtable keyed on the port: [ordered]
        # returns an OrderedDictionary, whose indexer binds an Int32 argument
        # to its *positional* overload, so $ports[6443] reads as "the 6444th
        # entry" and quietly returns nothing.
        $joinPorts = New-Object Collections.Generic.List[hashtable]
        $joinPorts.Add(@{ Port = 6443; Name = 'k3s apiserver' })
        if (-not $isAgent) {
            # An agent talks to the apiserver and is talked to on its own
            # kubelet port; it never joins the etcd cluster, so requiring
            # 2379-2380 of the server it joins would fail an install for a
            # reason that could not affect it.
            $joinPorts.Add(@{ Port = 2379; Name = 'etcd client' })
            $joinPorts.Add(@{ Port = 2380; Name = 'etcd peer' })
        }
        $joinPorts.Add(@{ Port = 10250; Name = 'kubelet' })
        if ($isAgent) {
            # Not node-to-node traffic - this script's own. An agent has no
            # kubeconfig, so Verify asks the join server every cluster-level
            # question over SSH with the same key. Unchecked, a missing route
            # there fails the run after the install rather than before it.
            $joinPorts.Add(@{ Port = 22; Name = 'SSH, which Verify uses to ask this server about the agent' })
        }
        foreach ($portProbe in $joinPorts) {
            if (-not (Test-TcpPort -IPAddress $JoinServer -Port $portProbe.Port)) {
                $failures.Add("-JoinServer $JoinServer isn't answering on $($portProbe.Port) ($($portProbe.Name)) from this machine. Confirm node 1 finished -ClusterInit, the address is right, and nothing is firewalling node-to-node traffic.")
            }
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed for '$VMName' ($IPAddress) with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Node:      $VMName ($IPAddress)"
    Write-Host "Role:      $roleDescription"
    Write-Host "Unit:      $serviceName.service"
    if ($NodeLabel.Count -gt 0) {
        Write-Host "Labels:    $($NodeLabel -join ', ')"
    }
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
            'echo ''--- service-agent'''
            'systemctl is-active k3s-agent 2>/dev/null || echo inactive'
            'echo ''--- version'''
            '(command -v k3s >/dev/null 2>&1 && k3s --version 2>/dev/null | head -n1) || echo ''k3s not installed'''
            'echo ''--- sysctl'''
            'sudo sysctl -n vm.max_map_count 2>/dev/null || echo 0'
            'echo ''--- sysctl-file'''
            'test -f {0} && {{ base64 -w0 {0}; echo; }} || echo NONE'
            'echo ''--- k3s-config'''
            'test -f {1} && {{ base64 -w0 {1}; echo; }} || echo NONE'
            'echo ''--- kubelet-dropin'''
            'sudo test -f {3} && {{ sudo base64 -w0 {3}; echo; }} || echo NONE'
            'echo ''--- journald-dropin'''
            'test -f {4} && {{ base64 -w0 {4}; echo; }} || echo NONE'
            'echo ''--- journal-usage'''
            'sudo journalctl --disk-usage 2>/dev/null || echo unknown'
            'echo ''--- kubeconfig-link'''
            'sudo test -L {2} && sudo readlink {2} || echo NONE'
            'echo ''--- end'''
        ) -join '; ') -f $sysctlDropInPath, $k3sConfigPath, $kubeconfigLinkPath, $kubeletDropInPath, $journaldDropInPath

    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 15
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdErr)"
    }

    # Both units are probed on every run, whichever one this dispatch asks
    # for: a node is a server or an agent, never both, and finding the other
    # one active is the difference between 'already done' and 'about to
    # install a second k3s over the top of the first'.
    $serverState = (Get-ProbeSection -Output $probe.StdOut -Name 'service').Trim()
    if (-not $serverState) { $serverState = 'unknown' }
    $agentState = (Get-ProbeSection -Output $probe.StdOut -Name 'service-agent').Trim()
    if (-not $agentState) { $agentState = 'unknown' }
    $serviceState = if ($isAgent) { $agentState } else { $serverState }
    $otherServiceState = if ($isAgent) { $serverState } else { $agentState }
    $installedVersion = (Get-ProbeSection -Output $probe.StdOut -Name 'version').Trim()
    if (-not $installedVersion) { $installedVersion = 'unknown' }
    $liveMaxMapCount = (Get-ProbeSection -Output $probe.StdOut -Name 'sysctl').Trim()
    $currentSysctlFile = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'sysctl-file')
    $currentK3sConfig = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'k3s-config')
    $currentKubeletDropIn = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'kubelet-dropin')
    $currentJournaldDropIn = ConvertFrom-RemoteFileProbe -Encoded (Get-ProbeSection -Output $probe.StdOut -Name 'journald-dropin')
    $currentJournalUsage = (Get-ProbeSection -Output $probe.StdOut -Name 'journal-usage').Trim()
    $currentKubeconfigLinkTarget = (Get-ProbeSection -Output $probe.StdOut -Name 'kubeconfig-link').Trim()

    Write-Host "$serviceName.service: $serviceState"
    Write-Host "k3s binary:  $installedVersion"

    # Not recoverable by installing anyway: `k3s server` and `k3s agent` share
    # /var/lib/rancher/k3s and would run two kubelets against one node.
    if ($otherServiceState -eq 'active') {
        throw @"
'$VMName' is already running $otherServiceName.service, but this dispatch asks for $serviceName.service.

A node is one or the other - both units claim /var/lib/rancher/k3s and the same kubelet, so installing the second over the first leaves two of them fighting for one node. Changing a node's role is a rebuild, not a reconfiguration.

To change it deliberately, uninstall the current role first, then re-dispatch:
  ssh $Username@$IPAddress sudo /usr/local/bin/$otherServiceName-uninstall.sh

If $otherServiceName is a server that is still an etcd member, remove it from the member list from a surviving node before uninstalling it, or it leaves a dead voter behind.
"@
    }

    $alreadyActive = $serviceState -eq 'active'
    # Captured before -Reinstall can flip $alreadyActive below - it gates the
    # Phase 2 checklist at the very end, which makes no sense to print for a
    # run that only reconciled the node settings on an already-active node and
    # never touched the install pipeline at all.
    $wasAlreadyActive = $alreadyActive
    if ($alreadyActive -and $Reinstall) {
        Write-Warning "-Reinstall: $serviceName is active on '$VMName' - running its uninstall script before reinstalling with today's inputs.$(if (-not $isAgent) { ' If this is a server node and other servers are still up, it will not have removed itself from the etcd member list first.' })"
        $uninstall = Invoke-NodeSsh @ssh -Command "test -x $uninstallScript && sudo $uninstallScript || echo 'no $uninstallScript found'" -ConnectTimeoutSec 60
        if ($uninstall.ExitCode -ne 0) {
            throw "$uninstallScript failed on $IPAddress (exit $($uninstall.ExitCode)):`n$($uninstall.StdOut)$($uninstall.StdErr)"
        }
        Write-Host '  uninstalled.'
        $alreadyActive = $false
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Node configuration'
    # ---------------------------------------------------------------- #
    #
    # The cluster plan Phase 6b.1's two node-level settings and the
    # node-storage plan Phase 2's two, applied here so they exist before k3s
    # ever starts on a fresh node - see those docs for why none of them can be
    # a chart-side or in-cluster fix. Idempotent: a re-run against a node that
    # already has all four reports nothing changed.
    #
    # Three of the four apply to an agent unchanged - the sysctl, the kubelet
    # drop-in (k3s runs kubelet from the same /var/lib/rancher/k3s/agent tree
    # on both roles) and the journald cap. The fourth, config.yaml, is
    # server-only and actively harmful on an agent; see its branch below.

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

    # Declared before the branch so the restart decision below can read it on
    # either path.
    $k3sConfigChanged = $false
    if ($isAgent) {
        # `k3s agent` reads this file too - configfilearg's After list names
        # both commands - but it only filters the keys it finds against the
        # *server* command's flags: ValidFlags has no 'agent' entry, and
        # stripInvalidFlags returns the list untouched for any command that
        # has none. So every key here is handed to the agent CLI verbatim, and
        # etcd-expose-metrics is not one of its flags. The unit then exits at
        # start on 'flag provided but not defined', which systemd reports as a
        # failed install rather than as the configuration mistake it is.
        #
        # An agent therefore gets no config.yaml at all. Nothing this script
        # manages needs to be in one on an agent, so there is no agent-shaped
        # version of it to write.
        if ($null -eq $currentK3sConfig) {
            Write-Host "$k3sConfigPath absent - correct for an agent (it is server-only here)."
        }
        elseif ($currentK3sConfig -eq $desiredK3sConfig) {
            # This node's own managed copy, left by an earlier server install
            # on the way to becoming an agent. Ours to remove.
            $removeK3sConfig = Invoke-NodeSsh @ssh -ConnectTimeoutSec 15 -Command (
                'sudo rm -f {0}' -f $k3sConfigPath
            )
            if ($removeK3sConfig.ExitCode -ne 0) {
                throw "Removing $k3sConfigPath on $IPAddress failed (exit $($removeK3sConfig.ExitCode)):`n$($removeK3sConfig.StdOut)$($removeK3sConfig.StdErr)"
            }
            $k3sConfigChanged = $true
            $configActions.Add("removed $k3sConfigPath (server-only - k3s agent would exit at start on its etcd-expose-metrics key)")
            Write-Host "$k3sConfigPath removed - server-only, and k3s agent would exit at start on it."
        }
        else {
            throw @"
$k3sConfigPath exists on '$VMName' with content this script did not write, and this dispatch installs an agent.

k3s hands every key in that file to 'k3s agent' without filtering it against the agent's own flags, so any server-only key in there stops the unit at start with 'flag provided but not defined' - an install that reports success and a node that never joins.

Read the file, move what belongs elsewhere, and delete it before re-dispatching:
  ssh $Username@$IPAddress cat $k3sConfigPath
"@
        }
    }
    else {
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
    }

    $kubeletDropInChanged = $currentKubeletDropIn -ne $desiredKubeletDropIn
    if (-not $kubeletDropInChanged) {
        Write-Host "$kubeletDropInPath already matches - not rewriting."
    }
    else {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($desiredKubeletDropIn))
        # mkdir -p rather than assuming the directory: on a node that has never
        # run k3s, nothing has created it yet. The chmod is on the leaf alone
        # and matches the 0700 k3s gives it, so a node configured before its
        # first install does not end up with a laxer directory than one
        # configured after - while the parents k3s also owns keep whatever
        # mkdir's umask gives them. k3s does not mind finding the file already
        # there when it writes its own defaults beside it.
        $writeKubeletDropIn = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            'sudo mkdir -p {0} && sudo chmod 0700 {0} && echo {1} | base64 -d | sudo tee {2} >/dev/null' -f $kubeletDropInDir, $encoded, $kubeletDropInPath
        )
        if ($writeKubeletDropIn.ExitCode -ne 0) {
            throw "Writing $kubeletDropInPath on $IPAddress failed (exit $($writeKubeletDropIn.ExitCode)):`n$($writeKubeletDropIn.StdOut)$($writeKubeletDropIn.StdErr)"
        }
        $configActions.Add("wrote $kubeletDropInPath (image GC $imageGcHighTarget/$imageGcLowTarget)")
        Write-Host "$kubeletDropInPath written (image GC high $imageGcHighTarget / low $imageGcLowTarget)."
    }

    if ($currentJournaldDropIn -eq $desiredJournaldDropIn) {
        Write-Host "$journaldDropInPath already matches - not rewriting.$(if ($currentJournalUsage -and $currentJournalUsage -ne 'unknown') { " Journal now: $currentJournalUsage" })"
    }
    else {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($desiredJournaldDropIn))
        # Three commands, because the cap alone changes nothing that already
        # exists: journald reads its configuration at start, and then only
        # enforces SystemMaxUse when it next rotates. The vacuum is what makes
        # the node match the file today rather than at some later write - the
        # existing journals are the 1.3-1.9 GB the plan's finding 5 counted.
        $writeJournald = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            'sudo mkdir -p /etc/systemd/journald.conf.d && echo {0} | base64 -d | sudo tee {1} >/dev/null && sudo systemctl restart systemd-journald && sudo journalctl --vacuum-size={2}' -f $encoded, $journaldDropInPath, $journaldCap
        )
        if ($writeJournald.ExitCode -ne 0) {
            throw "Writing $journaldDropInPath on $IPAddress failed (exit $($writeJournald.ExitCode)):`n$($writeJournald.StdOut)$($writeJournald.StdErr)"
        }
        $configActions.Add("wrote $journaldDropInPath (SystemMaxUse=$journaldCap), restarted systemd-journald and vacuumed")
        Write-Host "$journaldDropInPath written (SystemMaxUse=$journaldCap); journald restarted and vacuumed$(if ($currentJournalUsage -and $currentJournalUsage -ne 'unknown') { " (was: $currentJournalUsage)" })."
    }

    # Not one of the plans' settings - tracked in its own list so the
    # messaging below stays about what those steps actually assert.
    $kubeconfigActions = New-Object Collections.Generic.List[string]
    if ($isAgent) {
        # k3s never writes $k3sYamlPath on an agent - there is no apiserver on
        # this node to hand out credentials for - so the link would dangle for
        # the node's whole life rather than resolve on first start. The
        # ergonomic this buys a server (a bare `kubectl` from its console)
        # isn't available on an agent at all.
        Write-Host "Root's kubeconfig: skipped - an agent has no $k3sYamlPath to point at."
    }
    elseif ($currentKubeconfigLinkTarget -eq $k3sYamlPath) {
        Write-Host "$kubeconfigLinkPath already links to $k3sYamlPath - not rewriting."
    }
    else {
        # No target-exists check needed: a symlink to a file that doesn't
        # exist yet is valid and simply resolves once Install below runs for
        # the first time (or already has, on a re-run).
        $linkKubeconfig = Invoke-NodeSsh @ssh -ConnectTimeoutSec 15 -Command (
            'sudo mkdir -p {0} && sudo ln -sf {1} {2}' -f $kubeconfigLinkDir, $k3sYamlPath, $kubeconfigLinkPath
        )
        if ($linkKubeconfig.ExitCode -ne 0) {
            throw "Linking $kubeconfigLinkPath to $k3sYamlPath failed on $IPAddress (exit $($linkKubeconfig.ExitCode)):`n$($linkKubeconfig.StdOut)$($linkKubeconfig.StdErr)"
        }
        $kubeconfigActions.Add("linked $kubeconfigLinkPath -> $k3sYamlPath")
        Write-Host "$kubeconfigLinkPath -> $k3sYamlPath."
    }

    # Both of these are read once, at start: config.yaml by k3s itself and the
    # kubelet drop-in by the kubelet k3s launches. Neither is live-reloadable,
    # so a change to either on a running node does nothing until the service
    # comes back.
    $restartReasons = New-Object Collections.Generic.List[string]
    if ($k3sConfigChanged) { $restartReasons.Add("$k3sConfigPath") }
    if ($kubeletDropInChanged) { $restartReasons.Add("$kubeletDropInPath") }

    if ($alreadyActive -and $restartReasons.Count -gt 0) {
        # A restart, not the heavier -Reinstall (which also churns etcd
        # membership, per its own notes above). A fresh install below picks
        # both files up on its own first start, so this only fires when
        # reconfiguring a node that joined in an earlier phase.
        Write-Warning "Restarting $serviceName on '$VMName' to apply $($restartReasons -join ' and ').$(if ($isAgent) { ' This node stops running pods for the length of the restart; it is not an etcd member, so the cluster loses only its capacity.' } else { ' This takes one etcd member down for the length of the restart - wait for every node to show Ready (kubectl get nodes) before reconfiguring the next one.' })"
        $restart = Invoke-NodeSsh @ssh -Command "sudo systemctl restart $serviceName" -ConnectTimeoutSec 30
        if ($restart.ExitCode -ne 0) {
            throw "systemctl restart $serviceName failed on $IPAddress (exit $($restart.ExitCode)):`n$($restart.StdOut)$($restart.StdErr)"
        }
        $configActions.Add("restarted $serviceName to apply $($restartReasons -join ' and ')")
    }

    if ($configActions.Count -eq 0) {
        Write-Host 'Node configuration already matched - nothing changed.'
    }

    if ($alreadyActive) {
        Write-Host "$serviceName is already active on '$VMName' - skipping install (pass -Reinstall to force a clean reinstall). Proceeding to Verify."
    }
    else {
        # ---------------------------------------------------------------- #
        Write-Stage 'Install'
        # ---------------------------------------------------------------- #

        # Applied at registration. Kubelet writes them onto the Node object the
        # first time it creates one and never revisits them, which is why
        # Verify reconciles the same list against the live object afterwards -
        # this half only covers a node's first join.
        $labelArgs = @()
        foreach ($label in $NodeLabel) { $labelArgs += @('--node-label', $label) }

        $k3sArgs = if ($ClusterInit) {
            @('server', '--cluster-init', '--disable', 'servicelb', '--token', $Token) + $labelArgs
        }
        elseif ($isAgent) {
            # No --disable servicelb: that is a server flag, and handing it to
            # an agent fails the same way an inherited config.yaml does. The
            # disable is a cluster-wide decision the servers already made.
            @('agent', '--server', "https://${JoinServer}:6443", '--token', $Token) + $labelArgs
        }
        else {
            @('server', '--server', "https://${JoinServer}:6443", '--disable', 'servicelb', '--token', $Token) + $labelArgs
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

        Write-Host "Installing k3s $K3sVersion on '$VMName' as $(if ($isAgent) { 'an agent' } else { 'a server' }) ..."
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

    # Where a cluster-level question gets asked. A server can answer for
    # itself - k3s writes it a kubeconfig and it runs the apiserver. An agent
    # has neither, so `k3s kubectl` there fails against a default
    # localhost:8080; the same questions go to the server it joined, over SSH
    # with the same key material. That is what the port 22 preflight above is
    # for.
    $clusterSsh = if ($isAgent) {
        @{ IPAddress = $JoinServer; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }
    }
    else { $ssh }
    $clusterHost = if ($isAgent) { $JoinServer } else { $IPAddress }

    Write-Host "Waiting for $serviceName.service to be active ..."
    while ($true) {
        $status = Invoke-NodeSsh @ssh -Command "systemctl is-active $serviceName 2>/dev/null || echo inactive" -ConnectTimeoutSec 15
        if ($status.ExitCode -eq 0 -and $status.StdOut.Trim() -eq 'active') { break }
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $ReadyTimeoutMinutes min waiting for $serviceName.service to become active on $IPAddress. Inspect with: ssh $Username@$IPAddress sudo journalctl -u $serviceName -n 100"
        }
        Start-Sleep -Seconds 5
    }
    Write-Host "  $serviceName.service active."

    Write-Host "Waiting for node '$hostname' to report Ready$(if ($isAgent) { " (asked of $JoinServer)" }) ..."
    while ($true) {
        $nodeStatus = Invoke-NodeSsh @clusterSsh -Command "sudo k3s kubectl get node $hostname --no-headers 2>/dev/null" -ConnectTimeoutSec 15
        if ($nodeStatus.ExitCode -eq 0 -and $nodeStatus.StdOut -match '^\S+\s+Ready\b') { break }
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $ReadyTimeoutMinutes min waiting for '$hostname' to report Ready. Inspect with: ssh $Username@$clusterHost sudo k3s kubectl get nodes -o wide, and ssh $Username@$IPAddress sudo journalctl -u $serviceName -n 100"
        }
        Start-Sleep -Seconds 5
    }
    Write-Host '  node Ready.'

    if ($isAgent) {
        # The part-time-node plan Phase 1's exit criterion, and the one thing
        # that distinguishes this install from the server one at a glance: an
        # agent that somehow came up as a server would satisfy every other
        # check on this page. ROLES is the third column of `get node`.
        $nodeColumns = @($nodeStatus.StdOut.Trim() -split '\s+')
        $reportedRoles = if ($nodeColumns.Count -ge 3) { $nodeColumns[2] } else { '' }
        if ($reportedRoles -ne '<none>') {
            throw "'$hostname' is Ready but reports ROLES '$reportedRoles' rather than '<none>' - that is a control-plane node, not an agent. Confirm $serviceName.service is what is running (not k3s.service) and that this node was not previously installed as a server."
        }
        Write-Host '  ROLES: <none> (agent, as intended).'
    }

    # Each managed setting's own exit criterion - checked here rather than
    # left to a later gate script, so a run that reports success actually
    # satisfies them instead of finding out at 6b.5/6b.9, or a week later when
    # the node-storage plan's 4.2 is read, several steps and possibly days
    # later.
    Write-Host 'Verifying node settings ...'
    # sudo, not a bare 'sysctl': /usr/sbin (where Debian keeps the binary)
    # isn't on the non-root $Username's non-interactive SSH PATH, so a bare
    # call fails with 'command not found' and an empty StdOut - read as the
    # setting being unset even when it's already 262144. sudo's secure_path
    # includes /usr/sbin regardless of the invoking user's own PATH.
    $maxMapCheck = Invoke-NodeSsh @ssh -Command 'sudo sysctl -n vm.max_map_count' -ConnectTimeoutSec 15
    if ($maxMapCheck.ExitCode -ne 0 -or $maxMapCheck.StdOut.Trim() -ne '262144') {
        throw "vm.max_map_count on $IPAddress reads '$($maxMapCheck.StdOut.Trim())', not 262144. OpenSearch (6b.9) will refuse to start until this is fixed."
    }
    Write-Host '  vm.max_map_count: 262144.'

    if ($isAgent) {
        # Nothing to answer: an agent runs no etcd, which is most of the point
        # of it. The quorum alert 6b.8 evaluates is a property of the three
        # servers, and stays one however many agents join.
        Write-Host '  etcd metrics endpoint (:2381): skipped - an agent runs no etcd.'
    }
    else {
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
    }

    # The node-storage plan 2.1's exit criterion, asked of the kubelet that is
    # actually running rather than of the file it was supposed to read - the
    # whole failure mode this guards is a drop-in that is present and ignored.
    # Same short retry window as :2381 above and for the same reason: this
    # goes through the apiserver to kubelet's own :10250, which is up a moment
    # after the node reports Ready rather than at the same instant.
    $gcDeadline = (Get-Date).AddSeconds(60)
    $imageGcHigh = $null
    $imageGcLow = $null

    while ($true) {
        $configz = Invoke-NodeSsh @clusterSsh -ConnectTimeoutSec 20 -Command (
            "sudo k3s kubectl get --raw /api/v1/nodes/$hostname/proxy/configz"
        )
        if ($configz.ExitCode -eq 0 -and $configz.StdOut -match '"imageGCHighThresholdPercent"\s*:\s*(\d+)') {
            $imageGcHigh = [int]$Matches[1]
            if ($configz.StdOut -match '"imageGCLowThresholdPercent"\s*:\s*(\d+)') { $imageGcLow = [int]$Matches[1] }
            break
        }
        if ((Get-Date) -gt $gcDeadline) {
            throw "kubelet's live configuration on $IPAddress could not be read after 60s (/api/v1/nodes/$hostname/proxy/configz, asked of $clusterHost). Confirm $kubeletDropInPath is valid YAML and that kubelet started: ssh $Username@$IPAddress sudo journalctl -u $serviceName -n 100"
        }
        Start-Sleep -Seconds 5
    }
    if ($imageGcHigh -ne $imageGcHighTarget -or $imageGcLow -ne $imageGcLowTarget) {
        throw "kubelet on $IPAddress reports image GC thresholds $imageGcHigh/$imageGcLow, not $imageGcHighTarget/$imageGcLowTarget. $kubeletDropInPath was written but not taken - check it parses (kubelet ignores a drop-in it cannot read) and that $serviceName restarted after it landed: ssh $Username@$IPAddress sudo journalctl -u $serviceName -n 100"
    }
    Write-Host "  kubelet image GC: high $imageGcHigh / low $imageGcLow."

    # Reported rather than asserted: journald enforces SystemMaxUse when it
    # rotates, so a node can sit legitimately above the cap for a while after
    # the file lands. The write path above vacuums when it changes the file,
    # which is what makes this normally already true on the first run.
    $journalCheck = Invoke-NodeSsh @ssh -ConnectTimeoutSec 15 -Command 'sudo du -sb /var/log/journal 2>/dev/null | cut -f1'
    $journalRaw = $journalCheck.StdOut.Trim()
    if ($journalCheck.ExitCode -eq 0 -and $journalRaw -match '^\d+$') {
        $journalBytes = [int64]$journalRaw
        $journalMiB = [math]::Round($journalBytes / 1MB, 1)
        if ($journalBytes -gt ($journaldCapMiB * 1MB)) {
            Write-Warning "journald is holding ${journalMiB} MiB against a $journaldCap cap on $IPAddress. It trims on its next rotation; force it with: ssh $Username@$IPAddress sudo journalctl --vacuum-size=$journaldCap"
        }
        else {
            Write-Host "  journald: ${journalMiB} MiB of a $journaldCap cap."
        }
    }

    # --node-label above only lands at registration - kubelet sets those
    # labels when it first creates the Node object and never revisits them.
    # Reconciled here against the live object instead, which is the only thing
    # a nodeSelector or affinity rule ever reads, and the only way a re-run
    # against a node that already joined can change anything at all.
    $labelActions = New-Object Collections.Generic.List[string]
    if ($NodeLabel.Count -gt 0) {
        $labelJson = Invoke-NodeSsh @clusterSsh -ConnectTimeoutSec 20 -Command (
            "sudo k3s kubectl get node $hostname -o jsonpath='{.metadata.labels}'"
        )
        if ($labelJson.ExitCode -ne 0 -or -not $labelJson.StdOut.Trim()) {
            throw "Could not read '$hostname' labels from $clusterHost (exit $($labelJson.ExitCode)):`n$($labelJson.StdOut)$($labelJson.StdErr)"
        }
        $currentLabels = @{}
        foreach ($property in ($labelJson.StdOut.Trim() | ConvertFrom-Json).PSObject.Properties) {
            $currentLabels[$property.Name] = [string]$property.Value
        }

        foreach ($label in $NodeLabel) {
            # Split on the first = only: the validation on -NodeLabel already
            # forbids a second one, so this is about being explicit rather
            # than defensive.
            $key, $value = $label.Split('=', 2)
            if ($currentLabels.ContainsKey($key) -and $currentLabels[$key] -eq $value) {
                Write-Host "  label ${key}=${value}: already set."
                continue
            }
            $previous = if ($currentLabels.ContainsKey($key)) { " (was $($currentLabels[$key]))" } else { '' }
            # --overwrite so a changed value is a change rather than an error.
            # Nothing is ever removed here: kubelet, k3s and Longhorn all put
            # labels on this object too, and this script owns only the ones it
            # was handed.
            $applyLabel = Invoke-NodeSsh @clusterSsh -ConnectTimeoutSec 20 -Command (
                "sudo k3s kubectl label node $hostname $label --overwrite"
            )
            if ($applyLabel.ExitCode -ne 0) {
                throw "Applying label '$label' to '$hostname' from $clusterHost failed (exit $($applyLabel.ExitCode)):`n$($applyLabel.StdOut)$($applyLabel.StdErr)"
            }
            $labelActions.Add("${key}=${value}${previous}")
            Write-Host "  label ${key}=${value}: applied${previous}."
        }
    }

    $nodeList = Invoke-NodeSsh @clusterSsh -Command 'sudo k3s kubectl get nodes -o wide' -ConnectTimeoutSec 15
    Write-Host ''
    Write-Host $nodeList.StdOut.TrimEnd()

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "'$VMName' is running k3s $K3sVersion as $(if ($isAgent) { 'an agent' } else { 'a server' }) in ${elapsed} min." -ForegroundColor Green
    if (-not $ClusterInit -and -not $isAgent) {
        # Agents are exempt on the arithmetic, not by exception: joining one
        # adds no etcd member, so the quorum this warns about is whatever the
        # servers already made it.
        Write-Warning 'Two-node embedded etcd has worse availability than one node (tolerates zero losses, not one) - the cluster plan flags this build window as non-production until the third server rejoins in Phase 7.'
    }
    if ($configActions.Count -eq 0) {
        Write-Host 'Node settings: already matched - nothing changed.'
    }
    else {
        Write-Host 'Node settings changed:'
        foreach ($action in $configActions) { Write-Host "  - $action" }
    }
    if ($isAgent) {
        Write-Host "Root's kubeconfig: not applicable on an agent."
    }
    elseif ($kubeconfigActions.Count -eq 0) {
        Write-Host "Root's kubeconfig: already linked ($kubeconfigLinkPath -> $k3sYamlPath)."
    }
    else {
        Write-Host "Root's kubeconfig: $($kubeconfigActions -join '; ')."
    }

    if ($NodeLabel.Count -gt 0) {
        if ($labelActions.Count -eq 0) {
            Write-Host "Node labels: already as asked ($($NodeLabel -join ', '))."
        }
        else {
            Write-Host 'Node labels applied:'
            foreach ($action in $labelActions) { Write-Host "  - $action" }
        }
    }

    if ($isAgent -and -not $wasAlreadyActive) {
        Write-Host ''
        Write-Host 'Part-time-node plan Phase 1 - confirm and tick off in docs/plans/part-time-node.md:'
        Write-Host '  - the node shows <none> under ROLES                     (verified above)'
        Write-Host '  - it schedules pods (it is not tainted - that is the point of Phase 2)'
        Write-Host '  - Longhorn: set allowScheduling false on this node before it takes replicas (Phase 3.7)'
    }
    elseif (-not $wasAlreadyActive) {
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
            "| Role | $(if ($ClusterInit) { 'cluster-init (forms etcd)' } elseif ($isAgent) { "agent, joined $tick$JoinServer$tick" } else { "server, joined $tick$JoinServer$tick" }) |"
            "| Unit | $tick$serviceName.service$tick |"
            "| Address | $tick$IPAddress$tick |"
            "| k3s version | $tick$K3sVersion$tick ($script:K3sVersionSource) |"
            "| Node labels | $(if ($NodeLabel.Count -eq 0) { 'none' } else { (($NodeLabel | ForEach-Object { "$tick$_$tick" }) -join ', ') }) |"
            "| Reinstalled | $Reinstall |"
            "| Elapsed | ${elapsed} min |"
            ''
            "**Node settings:** $(if ($configActions.Count -eq 0) { 'already matched - nothing changed' } else { ($configActions -join '; ') })"
            ''
            "**Node labels:** $(if ($NodeLabel.Count -eq 0) { 'none asked for' } elseif ($labelActions.Count -eq 0) { 'already as asked' } else { ($labelActions -join '; ') })"
            ''
            "**Root's kubeconfig:** $(if ($isAgent) { 'not applicable on an agent' } elseif ($kubeconfigActions.Count -eq 0) { "already linked ($kubeconfigLinkPath -> $k3sYamlPath)" } else { ($kubeconfigActions -join '; ') })"
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
