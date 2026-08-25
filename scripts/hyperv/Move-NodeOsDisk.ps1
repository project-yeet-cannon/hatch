<#
.SYNOPSIS
    Moves one k3s node's OS disk onto the Hyper-V host's fastest volume and
    converts it from a dynamic VHDX to a fixed one of a larger size, one node
    at a time, with the cluster kept at quorum throughout.

.DESCRIPTION
    The node storage plan (docs/plans/node-storage.md) Phase 1, step 1.2, and
    the operation steps 1.4, 1.5 and 1.8 each run once. Three findings meet in
    this script: etcd fsyncs its write-ahead log before acknowledging any
    Kubernetes API write, that log is on the node's root filesystem, and that
    filesystem is a *dynamic* VHDX on the slowest volume each host owns. The
    fix is not a tuning knob - it is moving one file.

    Modelled on ..\..\k3s\Initialize-NodeStorage.ps1 and Add-BulkDisk.ps1: the
    same SSH mechanics from lib\AerieSsh.ps1, the same probe-once-then-decide
    structure, the same posture that a surprise is a reason to stop rather
    than a thing to work around. What is different is that this one runs on
    the *host* rather than only against the node, because Convert-VHD and
    Set-VMHardDiskDrive are host operations - so it needs Hyper-V and an
    elevated shell, and it reaches the cluster through the node's own
    `k3s kubectl`.

    Three modes, mutually exclusive:

      (default)          Migrate. Drain, stop, resize, convert-and-relocate,
                         repoint, boot, grow the filesystem, uncordon.
      -Rollback          Repoint the VM back at the source VHDX this script
                         recorded. The undo, for as long as that file exists.
      -RemoveSourceDisk  Delete the recorded source VHDX after the soak. The
                         only mode that deletes anything.

    Migrate stages:
      1. Preflight  - elevated, Hyper-V present, the VM exists, its OS disk is
                      unambiguous, the destination volume has room, the SSH
                      key resolves and the node answers port 22. Where a
                      checkpoint chain exists this resolves *through* it to
                      the disk underneath, and plans the merge rather than
                      performing it.
      2. Inspect    - one read-only probe collects the node's identity, its
                      block-device tree, its partition table, its disk usage
                      and its view of the cluster. A second probe reads etcd's
                      per-member health. Everything below is decided from that
                      snapshot and printed before it is acted on.
                      -PreflightOnly stops here.
      3. Packages   - cloud-guest-utils, if growpart isn't already there. Done
                      while the node is still up and still has a network,
                      because after stage 5 it has neither.
      4. Drain      - `kubectl drain --ignore-daemonsets --delete-emptydir-data`,
                      issued from a *peer* node, since this one is about to go
                      away.
      5. Stop       - graceful Stop-VM, then wait for Off. A node that will
                      not stop is not force-killed here; that is an operator's
                      decision about an etcd member, not a script's.

                      Then, and only then, the checkpoint merge. Merging
                      rewrites every block the .avhdx holds, and against a
                      *running* node - on a host whose VM volume backs both
                      the OS disk and the node's Longhorn data disk - it
                      starves the guest, kills Longhorn's instance-manager and
                      starts a rebuild storm on the same saturated disk. With
                      the VM off there is no guest to starve.
      6. Disk       - Resize-VHD on the source (instant, and cheap to undo),
                      then one Convert-VHD pass that both writes a fixed disk
                      and puts it on the destination volume. This is the long
                      step. The source is never modified beyond its virtual
                      size and never deleted.
      7. Repoint    - Set-VMHardDiskDrive at the same controller slot, then
                      reassert the firmware boot entry, then Start-VM.
      8. Grow       - wait for SSH, `growpart` the root partition and
                      `resize2fs` the filesystem, online.
      9. Verify     - re-read the filesystem size from the node, wait for the
                      node to report Ready and for every etcd member to report
                      healthy again, then uncordon.

    Idempotent in the way that matters at 2am: a re-run against a VM already
    booted from a fixed disk of the right size on the destination volume
    reports that and changes nothing.

    **No host name and no node name from this script reaches the repo.** Which
    physical machine is which, and which volume on it is the fast one, are
    arguments here and facts about one installation. See docs/ethos.md.

.PARAMETER DestinationPath
    The directory on the host's *fastest* volume that VM disks go under - the
    counterpart of New-AerieVM.ps1's -OsDiskPath, and normally the boot
    volume rather than the bulk one. The disk lands at
    <DestinationPath>\<VMName>\os-disk.vhdx.

    Nothing here guesses which volume is fastest. The plan measured it per
    host from windows_exporter; this takes the answer as an argument.

.PARAMETER SizeGB
    Virtual size of the new fixed disk, binary as everywhere else in this tree
    (100 means 100 GiB). Default 100, which is also what New-AerieVM.ps1 now
    builds a fresh node's OS disk at - a node migrated by this script and a
    node provisioned today land on the same number.

    A fixed disk consumes this much of the destination volume immediately, so
    the free-space refusal below is not advisory. On a host whose fast volume
    is tight, pass a smaller value rather than a smaller margin.

.PARAMETER PeerIPAddress
    Another node in the same cluster, used to issue the drain and to watch the
    target come back - the target itself cannot answer either question while
    it is off. Discovered from the target's own `kubectl get nodes` when not
    given; pass it when discovery would be ambiguous or when the target's
    kubectl cannot be trusted.

.PARAMETER Rollback
    Repoints the VM back at the source VHDX recorded in the sidecar beside the
    migrated disk, and boots it. The migrated disk is left on the destination
    volume for inspection; deleting it is an operator's call.

    This is the whole reason step 4.1 exists as a separate step: the undo is
    available for exactly as long as the source file is.

.PARAMETER RemoveSourceDisk
    Plan step 4.1. Deletes the source VHDX recorded in the sidecar, after
    asserting the VM is running from the migrated disk, that the node is Ready
    and every etcd member is healthy, and that the soak has elapsed. -Force
    waives the soak and nothing else.

.PARAMETER Force
    Allows a migration to proceed against a node that is not currently Ready,
    and waives -RemoveSourceDisk's soak. It never widens the three hard
    refusals: quorum, destination free space, and never deleting a source disk
    in a migrate run.

.EXAMPLE
    # The proving run, on a host with room
    .\Move-NodeOsDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.21 `
        -DestinationPath C:\aerie\VMs -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # See what it would do, without draining anything
    .\Move-NodeOsDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.21 `
        -DestinationPath C:\aerie\VMs -SshPrivateKeyPath ~\.ssh\id_ed25519 -PreflightOnly

.EXAMPLE
    # The constrained host, once its boot volume has been audited
    .\Move-NodeOsDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.23 `
        -DestinationPath C:\aerie\VMs -SizeGB 64 -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # It went wrong. Put it back.
    .\Move-NodeOsDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.21 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519 -Rollback

.EXAMPLE
    # A week later, step 4.1
    .\Move-NodeOsDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.21 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519 -RemoveSourceDisk
#>
#Requires -Modules Hyper-V
#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Move')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$')]
    [string]$VMName,

    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [Parameter(Mandatory, ParameterSetName = 'Move')]
    [string]$DestinationPath,

    [Parameter(ParameterSetName = 'Move')]
    [ValidateRange(8, 4096)]
    [int]$SizeGB = 100,

    [Parameter(Mandatory, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [Parameter(Mandatory, ParameterSetName = 'RemoveSource')]
    [switch]$RemoveSourceDisk,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$PeerIPAddress,

    # Common to every mode, because every mode either converts the disk or
    # repoints the VM at a different one, and a checkpoint makes both wrong.
    [switch]$MergeCheckpoints,

    [switch]$Force,

    [Parameter(ParameterSetName = 'Move')]
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\AerieSsh.ps1')
. (Join-Path $PSScriptRoot '..\k3s\lib\AerieNodeDisk.ps1')

# None of these are parameters, and for the same reason Add-BulkDisk.ps1 fixes
# its mount point and volume group: each has to agree with something decided
# elsewhere, and a value that varies per run is a silent mismatch rather than
# an error.
#
#   $OsDiskFileName    - what New-AerieVM.ps1 names the OS disk, and what an
#                        operator reading the destination directory will look
#                        for. Keeping the name identical means the on-disk
#                        layout in scripts/hyperv/README.md still describes a
#                        migrated host.
#   $FixedDiskFileName - the name the new disk takes when the destination is
#                        the directory the old one is already in. Convert-VHD
#                        cannot write the file it is reading, and a node whose
#                        VM volume is already the fastest one that host owns
#                        needs the fixed conversion without the move. The
#                        sidecar records both names, so nothing is ambiguous.
#   $SidecarFileName   - the record of where this disk came from. It is the
#                        rollback path and step 4.1's only source of truth
#                        about which file is safe to delete.
#   $FreeMarginGB      - the plan's refusal: never leave a Windows boot volume
#                        with less than this after a fixed disk lands on it.
#                        Not a parameter, because the way to fit on a tight
#                        volume is a smaller -SizeGB, not a thinner margin.
#   $SoakDays          - step 4.1's soak before a source disk may be deleted.
$OsDiskFileName = 'os-disk.vhdx'
$FixedDiskFileName = 'os-disk-fixed.vhdx'
$SidecarFileName = 'os-disk-migration.json'
$FreeMarginGB = 40
$SoakDays = 7

# Where k3s keeps the etcd client material. The health probe below uses the
# server-client cert rather than etcdctl, which k3s does not ship.
$EtcdCertDir = '/var/lib/rancher/k3s/server/tls/etcd'
$EtcdCurlOpts = "--cacert $EtcdCertDir/server-ca.crt --cert $EtcdCertDir/server-client.crt --key $EtcdCertDir/server-client.key"

# The plan's gate, as a query. p99 of etcd's write-ahead-log fsync, per
# member, over the last 15 minutes - the same number the alerts fire on and
# the same one findings 1 and 1.6 are stated in. Read through the API server's
# service proxy exactly as Test-Observability.ps1 does, so this needs no
# ingress, no port-forward and no credentials the node does not already have.
$FsyncP99Query = 'histogram_quantile(0.99, sum(rate(etcd_disk_wal_fsync_duration_seconds_bucket[15m])) by (le, instance))'

function Get-VmOsDisk {
    <#
    .SYNOPSIS
        Identifies which of a VM's virtual hard disks is the one it boots
        from, and refuses rather than guessing.

    .DESCRIPTION
        Not by file name. `os-disk.vhdx` is a convention New-AerieVM.ps1
        follows and nothing enforces, and this script's next action is to
        rewrite whatever it picks. The authority is the firmware boot order:
        Gen 2 VMs boot the first entry of type Drive, and New-AerieVM.ps1 sets
        it explicitly at create time.

        The guest is cross-checked against this separately, in the inspect
        stage: the disk holding / in the guest has to be the same size as the
        VHDX picked here. Two independent agreements, because the failure mode
        of getting this wrong is converting a node's Longhorn data disk.
    #>
    param([Parameter(Mandatory)][string]$VMName)

    $drives = @(Get-VMHardDiskDrive -VMName $VMName)
    if ($drives.Count -eq 0) {
        throw "VM '$VMName' has no virtual hard disks attached."
    }

    $bootEntries = @((Get-VMFirmware -VMName $VMName).BootOrder | Where-Object { $_.BootType -eq 'Drive' })
    $bootDisks = New-Object Collections.Generic.List[psobject]
    foreach ($entry in $bootEntries) {
        $device = $entry.Device
        if ($null -eq $device) { continue }
        if ($device.PSObject.Properties['Path']) {
            $match = @($drives | Where-Object { $_.Path -eq $device.Path })
            if ($match.Count -eq 1) { $bootDisks.Add($match[0]) }
        }
    }

    if ($bootDisks.Count -eq 0) {
        $listed = ($drives | ForEach-Object { $_.Path }) -join ', '
        throw "VM '$VMName' has no hard disk in its firmware boot order, so which of its $($drives.Count) disk(s) is the OS disk cannot be established: $listed. Set it with Set-VMFirmware -FirstBootDevice before running this."
    }

    return $bootDisks[0]
}

function Get-BaseVhd {
    <#
    .SYNOPSIS
        Walks a VHDX's parent chain to the disk at the bottom of it, and
        reports the value -DestinationPath would need for a conversion in
        place.

    .DESCRIPTION
        Two questions an operator has to answer before dispatching, and both
        are easy to get wrong by hand. What is the real disk under a
        checkpoint - the drive Hyper-V reports is the .avhdx, not the file
        that matters. And what does -DestinationPath have to say to mean
        "where it already is" - the answer is the *grandparent* of the disk
        file, since this script appends <VMName> to it, and a path that is
        one component off is a move nobody asked for rather than an error.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $path = $Path
    $vhd = Get-VHD -Path $path
    $depth = 0
    while ($vhd.VhdType -eq 'Differencing' -and $vhd.ParentPath -and $depth -lt 32) {
        $path = $vhd.ParentPath
        $vhd = Get-VHD -Path $path
        $depth++
    }
    $diskDir = Split-Path -Parent $path
    return [pscustomobject]@{
        Path               = $path
        Vhd                = $vhd
        ChainDepth         = $depth
        # What to type into -DestinationPath / the workflow's
        # destination_path to mean "leave it on this volume".
        InPlaceDestination = Split-Path -Parent $diskDir
    }
}

function Test-SamePath {
    <#
    .SYNOPSIS
        Whether two paths name the same file or directory.

    .DESCRIPTION
        'D:\aerie\VMs' and 'D:\Aerie\VMs\' are the same directory, and
        whether they are is the difference between a move and a conversion in
        place - which is the difference between Convert-VHD working and
        Convert-VHD being asked to write the file it is reading.
    #>
    param(
        [Parameter(Mandatory)][string]$A,
        [Parameter(Mandatory)][string]$B
    )
    $separators = @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return [string]::Equals(
        [IO.Path]::GetFullPath($A).TrimEnd($separators),
        [IO.Path]::GetFullPath($B).TrimEnd($separators),
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-DestinationVolumeFree {
    <#
    .SYNOPSIS
        Free bytes on the volume a path is rooted at, whether or not the path
        exists yet.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $volume = Get-Volume -FilePath $Path -ErrorAction SilentlyContinue
    if (-not $volume) {
        $driveLetter = [IO.Path]::GetPathRoot($Path).TrimEnd('\', ':')
        if ($driveLetter) { $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction SilentlyContinue }
    }
    if (-not $volume) {
        throw "Can't inspect the volume for '$Path'. Does that drive exist on $env:COMPUTERNAME?"
    }
    return [pscustomobject]@{
        DriveLetter = $volume.DriveLetter
        Free        = [int64]$volume.SizeRemaining
        Size        = [int64]$volume.Size
    }
}

function Invoke-NodeKubectl {
    <#
    .SYNOPSIS
        Runs one `k3s kubectl` command on a node and returns stdout, throwing
        with the node's own words on failure.

    .DESCRIPTION
        The host has no kubeconfig - see docs/ethos.md on not putting cluster
        credentials on a hypervisor - so every cluster read and write in this
        script goes through a node's own `sudo k3s kubectl`. Which node
        matters: anything issued while the target is off has to go to a peer.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Ssh,
        [Parameter(Mandatory)][string]$Arguments,
        [int]$ConnectTimeoutSec = 30
    )

    $result = Invoke-NodeSsh @Ssh -Command "sudo k3s kubectl $Arguments" -ConnectTimeoutSec $ConnectTimeoutSec
    if ($result.ExitCode -ne 0) {
        throw "kubectl $Arguments failed on $($Ssh.IPAddress) (exit $($result.ExitCode)):`n$($result.StdOut)$($result.StdErr)"
    }
    return $result.StdOut
}

function ConvertTo-ClusterNodeList {
    <#
    .SYNOPSIS
        Turns `kubectl get nodes -o json` into the four facts this script
        reasons about: name, address, readiness, and whether the node carries
        an etcd member.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Json)

    $nodes = New-Object Collections.Generic.List[psobject]
    if ([string]::IsNullOrWhiteSpace($Json)) { return $nodes }

    $parsed = $Json | ConvertFrom-Json
    foreach ($item in @(Get-Field $parsed 'items' | Where-Object { $_ })) {
        $metadata = Get-Field $item 'metadata'
        $status = Get-Field $item 'status'
        $labels = if ($metadata) { Get-Field $metadata 'labels' } else { $null }

        $addresses = if ($status) { @(Get-Field $status 'addresses' | Where-Object { $_ }) } else { @() }
        $internalIp = ''
        foreach ($address in $addresses) {
            if ([string](Get-Field $address 'type') -eq 'InternalIP') { $internalIp = [string](Get-Field $address 'address'); break }
        }

        $conditions = if ($status) { @(Get-Field $status 'conditions' | Where-Object { $_ }) } else { @() }
        $ready = $false
        foreach ($condition in $conditions) {
            if ([string](Get-Field $condition 'type') -eq 'Ready') { $ready = ([string](Get-Field $condition 'status') -eq 'True'); break }
        }

        $spec = Get-Field $item 'spec'
        $nodes.Add([pscustomobject]@{
                Name          = [string](Get-Field $metadata 'name')
                InternalIp    = $internalIp
                Ready         = $ready
                IsEtcdMember  = $null -ne $labels -and $null -ne (Get-Field $labels 'node-role.kubernetes.io/etcd')
                Unschedulable = $null -ne $spec -and [bool](Get-Field $spec 'unschedulable')
            })
    }
    return $nodes
}

function Get-EtcdMemberHealth {
    <#
    .SYNOPSIS
        Asks every etcd member whether it is healthy, from one node that can
        reach them all.

    .DESCRIPTION
        This is the input to the one refusal that cannot be waived: a node is
        not taken down while the cluster would lose quorum without it.

        etcd serves /health on its client port, so the check is a curl with
        k3s's own client certificate rather than etcdctl, which k3s does not
        ship. Members are addressed by the InternalIP of the nodes labelled
        node-role.kubernetes.io/etcd - k3s puts each member's client URL on
        exactly that address, and the server certificate covers it.

        If the *local* member cannot be reached this way at all, the certs are
        somewhere else or this is not an embedded-etcd cluster, and the caller
        is told the probe did not run rather than being told everything is
        fine. Silence and health are not the same answer.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Ssh,
        [psobject[]]$EtcdNodes = @()
    )

    $lines = New-Object Collections.Generic.List[string]
    $lines.Add('echo ''--- local''')
    $lines.Add("sudo curl -s -m 5 $EtcdCurlOpts https://127.0.0.1:2379/health || echo unreachable")
    foreach ($node in $EtcdNodes) {
        $lines.Add("echo '--- member-$($node.Name)'")
        $lines.Add("sudo curl -s -m 5 $EtcdCurlOpts https://$($node.InternalIp):2379/health || echo unreachable")
    }
    $lines.Add('echo ''--- end''')

    $result = Invoke-NodeSsh @Ssh -Command ($lines -join '; ') -ConnectTimeoutSec 60
    $local = (Get-ProbeSection -Output $result.StdOut -Name 'local').Trim()
    $probeRan = $local -match '"health"\s*:\s*"?true'

    $members = New-Object Collections.Generic.List[psobject]
    foreach ($node in $EtcdNodes) {
        $body = (Get-ProbeSection -Output $result.StdOut -Name "member-$($node.Name)").Trim()
        $members.Add([pscustomobject]@{
                Name    = $node.Name
                Address = $node.InternalIp
                Healthy = [bool]($body -match '"health"\s*:\s*"?true')
                Detail  = if ($body) { $body } else { 'no response' }
            })
    }

    return [pscustomobject]@{
        ProbeRan = $probeRan
        Members  = @($members)
        Healthy  = @($members | Where-Object { $_.Healthy })
    }
}

function Get-EtcdFsyncP99 {
    <#
    .SYNOPSIS
        Reads the plan's gate - p99 write-ahead-log fsync per etcd member -
        off the live Prometheus, best effort.

    .DESCRIPTION
        Best effort on purpose, and the only thing in this script that is:
        this is the number step 1.1 records and step 1.6 gates on, but a
        Prometheus that is scraping badly, or an observability stack that is
        itself mid-restart, must not be able to abort a maintenance window
        that is already underway. It is measurement, not control.
    #>
    param([Parameter(Mandatory)][hashtable]$Ssh)

    $readings = New-Object Collections.Generic.List[psobject]
    try {
        $encoded = [Uri]::EscapeDataString($FsyncP99Query)
        $path = "/api/v1/namespaces/observability/services/prometheus-operated:9090/proxy/api/v1/query?query=$encoded"
        $json = Invoke-NodeKubectl -Ssh $Ssh -Arguments "get --raw $path"
        $parsed = $json | ConvertFrom-Json
        $data = Get-Field $parsed 'data'
        if (-not $data) { return $null }
        foreach ($sample in @(Get-Field $data 'result' | Where-Object { $_ })) {
            $metric = Get-Field $sample 'metric'
            $value = @(Get-Field $sample 'value' | Where-Object { $null -ne $_ })
            if ($value.Count -lt 2) { continue }
            $seconds = 0.0
            if (-not [double]::TryParse([string]$value[1], [ref]$seconds)) { continue }
            $readings.Add([pscustomobject]@{
                    Instance = [string](Get-Field $metric 'instance')
                    Millis   = [math]::Round($seconds * 1000, 1)
                })
        }
    }
    catch {
        Write-Warning "Couldn't read etcd fsync p99 from Prometheus: $($_.Exception.Message) The migration does not depend on this; record the number by hand for the plan's 1.6 gate."
        return $null
    }
    return @($readings | Sort-Object Instance)
}

function Wait-NodeSshReady {
    <#
    .SYNOPSIS
        Blocks until a node answers SSH and calls itself the expected name.

    .DESCRIPTION
        Not Wait-AerieNodeReady, which waits for cloud-init and the reboot
        cloud-init triggers on itself - both already long past on a node that
        has been in the cluster for months. What is being waited for here is
        an ordinary boot, and the hostname check is what distinguishes "the
        node is back" from "something else now holds that address".
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Ssh,
        [Parameter(Mandatory)][string]$ExpectedHostname,
        [int]$TimeoutMinutes = 15,
        [int]$PollSeconds = 10
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort -IPAddress $Ssh.IPAddress -Port 22) {
            $probe = Invoke-NodeSsh @Ssh -Command 'hostnamectl --static 2>/dev/null || hostname' -ConnectTimeoutSec 15
            if ($probe.ExitCode -eq 0) {
                $reported = $probe.StdOut.Trim()
                if ($reported -eq $ExpectedHostname) { return }
                throw "$($Ssh.IPAddress) came back calling itself '$reported', not '$ExpectedHostname'."
            }
            $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
            if ($permanentReason) {
                throw "SSH to $($Ssh.IPAddress) as '$($Ssh.User)' failed: $permanentReason`n$($probe.StdErr)"
            }
        }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "Timed out after $TimeoutMinutes min waiting for $($Ssh.IPAddress) to answer SSH as '$ExpectedHostname'. The VM is started; watch its console with: vmconnect localhost $ExpectedHostname"
}

function Wait-ClusterNodeReady {
    <#
    .SYNOPSIS
        Blocks until a peer's view of the cluster reports the named node
        Ready.

    .DESCRIPTION
        Asked of a peer rather than of the node itself, and that is the point:
        a node whose own kubelet is up but whose etcd member has not rejoined
        will answer its own kubectl and still not be part of the cluster.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Ssh,
        [Parameter(Mandatory)][string]$NodeName,
        [int]$TimeoutMinutes = 15,
        [int]$PollSeconds = 10
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastSeen = 'no answer yet'
    while ((Get-Date) -lt $deadline) {
        try {
            $json = Invoke-NodeKubectl -Ssh $Ssh -Arguments 'get nodes -o json'
            $node = @(ConvertTo-ClusterNodeList -Json $json | Where-Object { $_.Name -eq $NodeName })
            if ($node.Count -eq 1) {
                if ($node[0].Ready) { return $node[0] }
                $lastSeen = 'NotReady'
            }
            else { $lastSeen = 'not in the node list' }
        }
        catch {
            $lastSeen = $_.Exception.Message
        }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "Timed out after $TimeoutMinutes min waiting for node '$NodeName' to report Ready (last seen: $lastSeen). The VM is up; the node is cordoned and has not been uncordoned, so nothing will be scheduled onto it until that is understood."
}

function Read-MigrationSidecar {
    <#
    .SYNOPSIS
        Reads the record this script wrote beside a migrated disk.

    .DESCRIPTION
        -Rollback and -RemoveSourceDisk both act on a file this script is not
        told about: the *source* VHDX, which is deliberately left behind and
        which nothing else on the host now references. The sidecar is the only
        link between the two, so both modes read it rather than take a path
        from an operator who would be typing it from memory during an
        incident.
    #>
    param([Parameter(Mandatory)][string]$CurrentDiskPath)

    $path = Join-Path (Split-Path -Parent $CurrentDiskPath) $SidecarFileName
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "No migration record at '$path'. This VM's OS disk was not put there by this script, so there is no source disk it can name. If you know what the source was, repoint the VM by hand with Set-VMHardDiskDrive."
    }
    $record = Get-Content -Path $path -Raw | ConvertFrom-Json
    return [pscustomobject]@{
        Path   = $path
        Record = $record
    }
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$hostname = $VMName.ToLowerInvariant()
$actions = New-Object Collections.Generic.List[string]
$mode = $PSCmdlet.ParameterSetName

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
        $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey. This is the same key every other provisioning step uses.')
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

    # --- the VM, on this host ------------------------------------------ #
    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if (-not $vm) {
        $failures.Add("No VM named '$VMName' on $env:COMPUTERNAME. This step runs on the Hyper-V host that holds the node, not on any host - `runs-on` picks that host in the workflow.")
    }

    # Checkpoints turn one VHDX into a chain, and every mode here is wrong on
    # one: Convert-VHD on a member of a chain produces a disk silently missing
    # everything written since the checkpoint, and repointing a VM that has
    # checkpoints leaves them referencing a disk the VM no longer uses.
    $snapshots = @()
    if ($vm) {
        $snapshots = @(Get-VMSnapshot -VMName $VMName -ErrorAction SilentlyContinue)
        if ($snapshots.Count -gt 0 -and -not $MergeCheckpoints -and -not $PreflightOnly) {
            $listed = ($snapshots | ForEach-Object { "$($_.Name) [$($_.SnapshotType)], taken $($_.CreationTime.ToString('yyyy-MM-dd HH:mm'))" }) -join '; '
            $failures.Add("VM '$VMName' has $($snapshots.Count) checkpoint(s): $listed. Its OS disk is a differencing chain while they exist. Pass -MergeCheckpoints to have this run merge them after the node is drained and the VM is off - or merge them by hand with Remove-VMSnapshot first. (-PreflightOnly reports a chain rather than refusing on one.)")
        }
    }

    # Thrown here rather than at the end, so a bad key or a missing VM is
    # reported before the merge below changes anything about the VM.
    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed for '$VMName' on $env:COMPUTERNAME with $($failures.Count) problem(s):`n$detail"
    }

    # -PreflightOnly promises it changes nothing, and both a merge and the
    # setting change below are changes. So it reports and stops instead -
    # which is the more useful answer anyway, since "this VM has been running
    # on a differencing disk since August" is a finding, not a nuisance.
    $autoCheckpointsOn = $null -ne $vm.PSObject.Properties['AutomaticCheckpointsEnabled'] -and $vm.AutomaticCheckpointsEnabled
    if ($PreflightOnly -and ($snapshots.Count -gt 0 -or $autoCheckpointsOn)) {
        Write-Host ''
        if ($snapshots.Count -gt 0) {
            Write-Host "'$VMName' has $($snapshots.Count) checkpoint(s), so its OS disk is a differencing chain:" -ForegroundColor Yellow
            foreach ($snapshot in $snapshots) {
                Write-Host "  - $($snapshot.Name) [$($snapshot.SnapshotType)], taken $($snapshot.CreationTime.ToString('yyyy-MM-dd HH:mm'))"
            }
            Write-Host '  Every guest write since then has gone to a dynamic .avhdx layered over the disk.'
            Write-Host '  -MergeCheckpoints merges it AFTER the drain, with the VM off. Merging against a live'
            Write-Host '  node starves the guest and takes Longhorn down with it, so this run will not do that.'
        }
        if ($autoCheckpointsOn) {
            Write-Host "'$VMName' has automatic checkpoints ENABLED, which is what creates one at every VM start." -ForegroundColor Yellow
            Write-Host '  A fixed disk underneath one of these is a dynamic differencing disk again, so this'
            Write-Host '  has to be off before the migration means anything.'
        }

        # Reported here too, because this return is the one an operator hits
        # *first* on a node that has been up a while - and the disk path and
        # the in-place destination are what they came for. Best effort: the
        # chain is intact and readable at this point, but nothing below this
        # line is worth failing a report over.
        try {
            $peek = Get-BaseVhd -Path (Get-VmOsDisk -VMName $VMName).Path
            Write-Host ''
            Write-Host "OS disk:   $($peek.Path)"
            Write-Host "           $($peek.Vhd.VhdType), $(Format-Size ([int64]$peek.Vhd.Size)) virtual$(if ($peek.ChainDepth -gt 0) { ", under $($peek.ChainDepth) differencing layer(s)" })"
            Write-Host "To convert it in place, pass:  -DestinationPath '$($peek.InPlaceDestination)'"
            Write-Host "                                (destination_path in the workflow; it is the parent of the"
            Write-Host "                                 per-VM folder, because <VMName> is appended to it)"
        }
        catch {
            Write-Warning "Couldn't read the OS disk chain to report its path: $($_.Exception.Message)"
        }

        Write-Host ''
        Write-Host 'A run with -MergeCheckpoints resolves both, then continues. -PreflightOnly changes nothing, so it stops here.' -ForegroundColor Yellow
        Write-Host 'Order in a real run: drain -> stop -> merge -> resize -> convert -> repoint -> boot -> grow.'
        return
    }

    # --- automatic checkpoints, which would undo this entire plan ------- #
    #
    # Hyper-V's automatic checkpoints are created when a VM *starts* and
    # removed when it shuts down cleanly, so a VM that has been up for weeks
    # is running on a dynamic .avhdx layered over its disk - a third
    # allocation penalty on top of the two docs/plans/node-storage.md names,
    # and one that copies-on-write at block granularity.
    #
    # Disabling it is unconditional on a VM this script touches, and the
    # reason is not tidiness: a fixed VHDX underneath an automatic checkpoint
    # is a dynamic differencing disk again. Leaving the setting on would mean
    # the node came back from this migration, started, and immediately
    # reacquired the allocation cost the migration exists to remove - with
    # every measurement in the plan's 1.6 gate taken against it.
    #
    # It is safe to do here, on a live node: it is one config flag that takes
    # effect at the next start, and it moves no blocks. The *merge* is the
    # part that moves blocks, and that is why it is not here - see the Stop
    # stage.
    if ($autoCheckpointsOn) {
        Write-Warning "'$VMName' has automatic checkpoints enabled. Disabling: a fixed disk underneath an automatic checkpoint is a dynamic differencing disk again, so leaving this on would silently undo the migration at the next boot."
        Set-VM -Name $VMName -AutomaticCheckpointsEnabled $false
        $actions.Add('disabled automatic checkpoints')
        $vm = Get-VM -Name $VMName
    }

    # The drive Hyper-V reports is the head of the chain - an .avhdx while a
    # checkpoint exists. Everything below reasons about the disk at the
    # *bottom* of it, because that is the file that survives the merge in the
    # Stop stage and the file Convert-VHD will read. Its virtual size is the
    # same as the head's, which is what makes the guest cross-check in Inspect
    # valid either way.
    $currentDrive = $null
    $currentVhd = $null
    $baseDiskPath = $null
    $chainDepth = 0
    try {
        $currentDrive = Get-VmOsDisk -VMName $VMName
        $base = Get-BaseVhd -Path $currentDrive.Path
        $currentVhd = $base.Vhd
        $chainDepth = $base.ChainDepth
        $baseDiskPath = $base.Path
    }
    catch {
        $failures.Add($_.Exception.Message)
    }

    if ($chainDepth -gt 0 -and -not $MergeCheckpoints) {
        $failures.Add("'$($currentDrive.Path)' is a differencing disk $chainDepth layer(s) above '$baseDiskPath'. Pass -MergeCheckpoints to have this run merge the chain - after the node is drained and the VM is off, so the merge cannot starve a serving node - or merge it by hand with Remove-VMSnapshot first.")
    }

    # --- what each mode is going to act on ----------------------------- #
    $sourcePath = $null
    $newDiskPath = $null
    $sidecarPath = $null
    $sidecar = $null
    $alreadyMigrated = $false
    $inPlace = $false

    if ($mode -eq 'Move') {
        if ($DestinationPath -match '^\\\\') {
            $failures.Add("-DestinationPath '$DestinationPath' is a UNC path. A fixed VHDX on a network share is not what this plan measured and not what it recommends; give a local volume.")
        }
        $sourcePath = $baseDiskPath
        $destinationDir = Join-Path $DestinationPath $VMName
        $newDiskPath = Join-Path $destinationDir $OsDiskFileName

        if ($currentVhd) {
            # Not all three nodes need the same operation. Where a host's VM
            # volume already *is* the fastest large volume it owns, there is
            # nothing to move and the whole of the win is dynamic -> fixed,
            # done in place. Whether this run is that case is decided by
            # comparing directories, not by an operator remembering to say so.
            $inPlace = Test-SamePath -A (Split-Path -Parent $sourcePath) -B $destinationDir

            # Idempotence, and the only reason a re-run is safe to type twice:
            # a VM already booted from a fixed disk of the right size, in the
            # destination directory, is finished rather than half-done -
            # whichever of the two names that disk carries.
            $alreadyMigrated = $inPlace -and
                               ($currentVhd.VhdType -eq 'Fixed') -and
                               ([int64]$currentVhd.Size -ge ([int64]$SizeGB * 1GB))

            # Convert-VHD reads one file and writes another, so an in-place
            # conversion needs a second name. Both are recorded in the
            # sidecar; after step 4.1 deletes the source, the surviving disk
            # is the -fixed one and that record is what explains the name.
            if ($inPlace -and -not $alreadyMigrated) {
                $newDiskPath = Join-Path $destinationDir $FixedDiskFileName
                if (Test-SamePath -A $newDiskPath -B $sourcePath) {
                    $failures.Add("'$($currentDrive.Path)' is already the name this script converts *to*, and it is $($currentVhd.VhdType) at $(Format-Size $currentVhd.Size) rather than a finished Fixed ${SizeGB} GiB disk. That is a half-finished earlier run; sort it out by hand before re-running.")
                }
            }

            if (-not $alreadyMigrated -and [int64]$currentVhd.Size -gt ([int64]$SizeGB * 1GB)) {
                $failures.Add("'$sourcePath' is already $(Format-Size $currentVhd.Size), larger than the -SizeGB $SizeGB asked for. A VHDX can be grown online and shrunk only by moving data first, so this refuses rather than truncating a root filesystem.")
            }
            if (-not $alreadyMigrated -and (Test-Path $newDiskPath -PathType Leaf)) {
                $failures.Add("'$newDiskPath' already exists but the VM is not booted from it. That is either an abandoned run or another VM's disk; neither is safe to overwrite. Move it aside and re-run.")
            }
        }

        if (-not $alreadyMigrated) {
            try {
                # Measured on the destination volume, and correct for both
                # cases without a special case: the source disk is never
                # deleted here, so during and after the conversion both files
                # exist, and in the in-place case both are on this volume.
                $volume = Get-DestinationVolumeFree -Path $DestinationPath
                $needBytes = ([int64]$SizeGB * 1GB) + ([int64]$FreeMarginGB * 1GB)
                if ($volume.Free -lt $needBytes) {
                    $failures.Add("Volume $($volume.DriveLetter): has $(Format-Size $volume.Free) free; a ${SizeGB}GB fixed disk plus the ${FreeMarginGB}GB margin needs $(Format-Size $needBytes)$(if ($inPlace) { ", and the source disk stays on this same volume until -RemoveSourceDisk runs" }). This refusal is the plan's, and the way through it is a smaller -SizeGB or a different -DestinationPath, not a smaller margin - a fixed VHDX must never be the reason a Windows volume fills.")
                }
            }
            catch {
                $failures.Add($_.Exception.Message)
            }
        }
    }
    else {
        # -Rollback and -RemoveSourceDisk both read the record beside the
        # disk the VM is currently booted from.
        if ($currentDrive) {
            try {
                $sidecar = Read-MigrationSidecar -CurrentDiskPath $currentDrive.Path
                $sidecarPath = $sidecar.Path
                $sourcePath = [string](Get-Field $sidecar.Record 'sourcePath')
                $newDiskPath = [string](Get-Field $sidecar.Record 'destinationPath')

                if (-not (Test-SamePath -A $baseDiskPath -B $newDiskPath)) {
                    $failures.Add("The migration record at '$sidecarPath' describes '$newDiskPath', but VM '$VMName' boots from '$baseDiskPath'. Something moved this disk outside this script; resolve that by hand.")
                }
                if (-not $sourcePath -or -not (Test-Path $sourcePath -PathType Leaf)) {
                    $failures.Add("The source disk '$sourcePath' named in '$sidecarPath' is not there. $(if ($mode -eq 'Rollback') { 'There is nothing to roll back to - it has already been deleted.' } else { 'It has already been deleted; nothing to do.' })")
                }
            }
            catch {
                $failures.Add($_.Exception.Message)
            }
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed for '$VMName' on $env:COMPUTERNAME with $($failures.Count) problem(s):`n$detail"
    }

    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        # Not fatal for -Rollback: the whole reason to roll back may be that
        # the node no longer answers. It is fatal for the other two, which
        # both begin by asking the cluster's permission.
        if ($mode -eq 'Rollback') {
            Write-Warning "$IPAddress isn't answering on port 22. Rolling back anyway - a node that will not come up is the case this mode exists for. The drain below will be skipped."
        }
        else {
            throw "$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right."
        }
    }

    Write-Host "Host:      $env:COMPUTERNAME"
    Write-Host "VM:        $VMName ($IPAddress), currently $($vm.State)"
    Write-Host "OS disk:   $sourcePath"
    Write-Host "           $($currentVhd.VhdType), $(Format-Size $currentVhd.Size) virtual, $(Format-Size $currentVhd.FileSize) on disk, $($currentDrive.ControllerType) $($currentDrive.ControllerNumber):$($currentDrive.ControllerLocation)"
    if ($chainDepth -gt 0) {
        Write-Host "           behind $chainDepth checkpoint layer(s), head $($currentDrive.Path)"
        Write-Host "           the chain merges in the Stop stage, with the VM off - never against a serving node"
    }
    switch ($mode) {
        'Move' {
            if ($alreadyMigrated) {
                Write-Host "Target:    $newDiskPath - already there, already Fixed, already $(Format-Size $currentVhd.Size)."
            }
            elseif ($inPlace) {
                Write-Host "Target:    $newDiskPath, Fixed, ${SizeGB} GiB"
                Write-Host "           CONVERSION IN PLACE - the destination is the volume this disk is already on, so"
                Write-Host "           nothing moves and the whole of the change is dynamic -> fixed and 32 -> ${SizeGB} GiB."
            }
            else {
                Write-Host "Target:    $newDiskPath, Fixed, ${SizeGB} GiB (moved off $([IO.Path]::GetPathRoot($sourcePath).TrimEnd('\')))"
                Write-Host "           This is a MOVE. To convert in place on the volume it is already on instead,"
                Write-Host "           re-run with -DestinationPath '$(Split-Path -Parent (Split-Path -Parent $sourcePath))'."
            }
        }
        'Rollback' { Write-Host "Rollback:  back to $sourcePath, per $sidecarPath" }
        'RemoveSource' { Write-Host "Deleting:  $sourcePath, per $sidecarPath" }
    }
    if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    Write-Host 'Preflight OK.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-osdisk-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Inspect'
    # ---------------------------------------------------------------- #

    # One round trip, for the same reason its siblings take one: eight are
    # eight moments at which the node could change under the decision being
    # made about it. Single quotes only - Invoke-NodeSsh refuses a command
    # containing a double quote, because Windows PowerShell 5.1 would let
    # ssh.exe strip it and run a subtly different script on the node.
    $probeScript = @(
        'echo ''--- identity'''
        'hostnamectl --static 2>/dev/null || hostname'
        'echo ''--- root'''
        'findmnt -n -o SOURCE /'
        'echo ''--- rootdisk'''
        'lsblk -no PKNAME $(findmnt -n -o SOURCE /) | head -n 1'
        'echo ''--- lsblk'''
        'lsblk -J -b -o NAME,PATH,TYPE,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINT'
        'echo ''--- sfdisk'''
        'sudo sfdisk --json /dev/$(lsblk -no PKNAME $(findmnt -n -o SOURCE /) | head -n 1) 2>/dev/null || echo {}'
        'echo ''--- df'''
        'df -B1 --output=size,used,avail,pcent / | tail -n 1'
        'echo ''--- growpart'''
        'command -v growpart || echo missing'
        'echo ''--- k3s'''
        'command -v k3s || echo missing'
        'echo ''--- nodes'''
        'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
        'echo ''--- lhvolumes'''
        'sudo k3s kubectl -n longhorn-system get volumes.longhorn.io -o json 2>/dev/null || echo {}'
        'echo ''--- end'''
    ) -join '; '

    $nodeReachable = Test-TcpPort -IPAddress $IPAddress -Port 22
    $clusterNodes = @()
    $targetNode = $null
    $etcdNodes = @()
    $rootSource = ''
    $rootDisk = ''
    $rootPartitionNumber = 0
    $guestRootBytes = [int64]0
    $guestUsedBytes = [int64]0
    $guestAvailBytes = [int64]0
    $growpartPresent = $false
    $devices = @()
    $fsyncBefore = $null

    if ($nodeReachable) {
        $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 30
        if ($probe.ExitCode -ne 0) {
            $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
            throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdErr)"
        }

        # The node names itself before anything on this host is rewritten.
        $reportedHostname = (Get-ProbeSection -Output $probe.StdOut -Name 'identity').Trim()
        if ($reportedHostname -and $reportedHostname -ne $hostname) {
            throw "$IPAddress calls itself '$reportedHostname', but -VMName says '$hostname'. This step converts a VM's boot disk and drains a cluster node, so it stops rather than guessing which of the two is wrong."
        }

        $rootSource = (Get-ProbeSection -Output $probe.StdOut -Name 'root').Trim()
        $rootDisk = (Get-ProbeSection -Output $probe.StdOut -Name 'rootdisk').Trim()
        $growpartPresent = (Get-ProbeSection -Output $probe.StdOut -Name 'growpart').Trim() -ne 'missing'
        $k3sPath = (Get-ProbeSection -Output $probe.StdOut -Name 'k3s').Trim()
        $devices = ConvertTo-DeviceList -Json (Get-ProbeSection -Output $probe.StdOut -Name 'lsblk')

        if (-not $rootSource -or -not $rootDisk) {
            throw "Couldn't establish which device holds / on $IPAddress (findmnt said '$rootSource', lsblk said '$rootDisk'). Raw probe output:`n$($probe.StdOut)"
        }
        if ($k3sPath -eq 'missing') {
            throw "k3s is not on PATH on $IPAddress. This script reaches the cluster through the node's own 'sudo k3s kubectl' - the host deliberately has no kubeconfig."
        }

        # The second of the two independent agreements about which disk this
        # is. The first was the firmware boot order on the host; this one is
        # the guest's: the disk holding / has to be the size the VHDX says.
        # A node's Longhorn data disk is 200GB against an OS disk's 32, so a
        # mix-up is caught here rather than discovered after the convert.
        $guestRootDiskEntry = @($devices | Where-Object { $_.Path -eq "/dev/$rootDisk" -and $_.Type -eq 'disk' })
        if ($guestRootDiskEntry.Count -ne 1) {
            throw "lsblk on $IPAddress does not describe /dev/$rootDisk as a single disk. Raw probe output:`n$($probe.StdOut)"
        }
        $guestRootDiskBytes = [int64]$guestRootDiskEntry[0].Size
        $sizeGap = [math]::Abs($guestRootDiskBytes - [int64]$currentVhd.Size)
        if ($sizeGap -gt ([int64]$currentVhd.Size * 0.01)) {
            throw "The guest's root disk /dev/$rootDisk is $(Format-Size $guestRootDiskBytes), but the VHDX this host would convert - '$($currentDrive.Path)' - is $(Format-Size $currentVhd.Size). Those are not the same disk. Nothing has been changed; establish which disk the VM actually boots from before re-running."
        }

        # growpart only grows the last partition on a disk. Checked here, on
        # a live node, rather than discovered after the convert with the node
        # already down and the window already spent.
        $sfdiskJson = (Get-ProbeSection -Output $probe.StdOut -Name 'sfdisk').Trim()
        $partitionTable = $null
        if ($sfdiskJson -and $sfdiskJson -ne '{}') {
            $partitionTable = Get-Field ($sfdiskJson | ConvertFrom-Json) 'partitiontable'
        }
        if (-not $partitionTable) {
            throw "sfdisk could not read a partition table from /dev/$rootDisk on $IPAddress. growpart needs one, so this stops here."
        }
        $partitions = @(Get-Field $partitionTable 'partitions' | Where-Object { $_ })
        $rootPartition = @($partitions | Where-Object { [string](Get-Field $_ 'node') -eq $rootSource })
        if ($rootPartition.Count -ne 1) {
            throw "The partition table on /dev/$rootDisk does not contain exactly one entry for $rootSource. growpart is told a disk and a partition number, and there is no safe number to give it here."
        }
        $rootStart = [int64](Get-Field $rootPartition[0] 'start')
        $laterPartitions = @($partitions | Where-Object { [int64](Get-Field $_ 'start') -gt $rootStart })
        if ($laterPartitions.Count -gt 0) {
            $listed = ($laterPartitions | ForEach-Object { "$(Get-Field $_ 'node') at sector $(Get-Field $_ 'start')" }) -join ', '
            throw "$rootSource is not the last partition on /dev/$rootDisk - $listed sit(s) after it. growpart cannot extend into space it does not own, so the disk would grow and the filesystem would not. This needs a repartition, which is not what this script does."
        }
        if ($rootSource -notmatch '(\d+)$') {
            throw "Couldn't read a partition number off '$rootSource'. growpart takes a disk and a number."
        }
        $rootPartitionNumber = [int]$Matches[1]

        $dfFields = @((Get-ProbeSection -Output $probe.StdOut -Name 'df').Trim() -split '\s+' | Where-Object { $_ })
        if ($dfFields.Count -ge 3) {
            [void][int64]::TryParse($dfFields[0], [ref]$guestRootBytes)
            [void][int64]::TryParse($dfFields[1], [ref]$guestUsedBytes)
            [void][int64]::TryParse($dfFields[2], [ref]$guestAvailBytes)
        }

        $clusterNodes = @(ConvertTo-ClusterNodeList -Json (Get-ProbeSection -Output $probe.StdOut -Name 'nodes'))
        if ($clusterNodes.Count -eq 0) {
            throw "'sudo k3s kubectl get nodes' returned nothing usable on $IPAddress. (If sudo needs a password here, that is the actual failure.)"
        }
        $targetMatch = @($clusterNodes | Where-Object { $_.Name -eq $hostname })
        if ($targetMatch.Count -ne 1) {
            throw "The cluster does not contain exactly one node called '$hostname'. It has: $(($clusterNodes | ForEach-Object { $_.Name }) -join ', ')."
        }
        $targetNode = $targetMatch[0]
        $etcdNodes = @($clusterNodes | Where-Object { $_.IsEtcdMember -and $_.InternalIp })

        # --- Longhorn, which is the real constraint between two runs ---- #
        #
        # etcd quorum decides whether this node may go down at all. Longhorn
        # decides whether it may go down *now*: draining a node while a volume
        # is still rebuilding the replica it lost to the previous run can take
        # that volume to zero healthy replicas. The drain would usually be
        # refused by Longhorn's own PodDisruptionBudget, which is a safe
        # failure but an opaque one - a timeout 10 minutes into a maintenance
        # window rather than a sentence before it starts.
        #
        # This is also the answer to "how long do I wait between nodes?".
        # Nothing here is a fixed interval, because a rebuild's duration is a
        # function of how much data moved, not of the clock. Re-run, and it
        # either proceeds or names the volume it is waiting on.
        #
        # Only attached volumes are judged. A detached volume reports
        # robustness 'unknown', which is not a complaint about anything.
        $lhJson = (Get-ProbeSection -Output $probe.StdOut -Name 'lhvolumes').Trim()
        $degradedVolumes = @()
        if ($lhJson -and $lhJson -ne '{}') {
            foreach ($item in @(Get-Field ($lhJson | ConvertFrom-Json) 'items' | Where-Object { $_ })) {
                $vmeta = Get-Field $item 'metadata'
                $vstatus = Get-Field $item 'status'
                if (-not $vstatus) { continue }
                $state = [string](Get-Field $vstatus 'state')
                $robustness = [string](Get-Field $vstatus 'robustness')
                if ($state -eq 'attached' -and $robustness -ne 'healthy') {
                    $degradedVolumes += [pscustomobject]@{
                        Name       = [string](Get-Field $vmeta 'name')
                        State      = $state
                        Robustness = if ($robustness) { $robustness } else { 'unknown' }
                    }
                }
            }
        }

        if ($degradedVolumes.Count -gt 0) {
            $listed = ($degradedVolumes | ForEach-Object { "  - $($_.Name): $($_.State), robustness $($_.Robustness)" }) -join "`n"
            if ($Force) {
                Write-Warning "$($degradedVolumes.Count) Longhorn volume(s) are attached but not healthy, and -Force was passed. Proceeding.`n$listed"
            }
            else {
                throw "$($degradedVolumes.Count) Longhorn volume(s) are attached but not healthy, so this is not the moment to take a node down:`n$listed`n`nThis is usually the previous node's rebuild still running - wait for robustness to read healthy and re-run, there is no fixed interval to wait. Watch it with:`n  sudo k3s kubectl -n longhorn-system get volumes.longhorn.io -o custom-columns=NAME:.metadata.name,STATE:.status.state,ROBUSTNESS:.status.robustness`n`nPass -Force only if you know why a volume is degraded and that it does not depend on '$hostname'."
            }
        }
    }

    # --- the peer that will speak for the cluster ---------------------- #
    #
    # Every cluster question from stage 5 onwards has to be asked of a node
    # that is not the one being switched off. Discovered rather than named,
    # for the same reason nothing else here is named: which node is which is
    # a fact about one installation.
    $peer = $null
    $peerSsh = $null
    if ($nodeReachable) {
        $peerCandidates = @($clusterNodes |
                Where-Object { $_.Name -ne $hostname -and $_.Ready -and $_.InternalIp } |
                Sort-Object Name)
        if ($PeerIPAddress) {
            $named = @($clusterNodes | Where-Object { $_.InternalIp -eq $PeerIPAddress })
            if ($named.Count -ne 1) {
                throw "-PeerIPAddress $PeerIPAddress is not the address of exactly one node in this cluster. Nodes: $(($clusterNodes | ForEach-Object { "$($_.Name) ($($_.InternalIp))" }) -join ', ')."
            }
            if ($named[0].Name -eq $hostname) {
                throw "-PeerIPAddress $PeerIPAddress is the target node itself. The peer is what answers while the target is off."
            }
            $peer = $named[0]
        }
        elseif ($peerCandidates.Count -eq 0) {
            throw "No other Ready node in this cluster to issue the drain from. Nodes: $(($clusterNodes | ForEach-Object { "$($_.Name) $(if ($_.Ready) { 'Ready' } else { 'NotReady' })" }) -join ', '). Taking this one down would leave nothing to watch it come back."
        }
        else {
            $peer = $peerCandidates[0]
        }

        $peerSsh = @{ IPAddress = $peer.InternalIp; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

        # Proved now, while the target is still up and there is still a way
        # out. A peer that turns out to be unreachable after the target has
        # been switched off is an outage with no console but vmconnect.
        $peerProbe = Invoke-NodeSsh @peerSsh -Command 'hostnamectl --static 2>/dev/null || hostname' -ConnectTimeoutSec 20
        if ($peerProbe.ExitCode -ne 0 -or $peerProbe.StdOut.Trim() -ne $peer.Name) {
            throw "Couldn't reach peer node '$($peer.Name)' at $($peer.InternalIp) over SSH as '$Username' (exit $($peerProbe.ExitCode), it said '$($peerProbe.StdOut.Trim())'). Everything from the drain onwards is issued from this node, so it is proved before the target is touched.`n$($peerProbe.StdErr)"
        }
    }

    # --- quorum: the refusal that -Force does not widen ---------------- #
    $etcdHealth = $null
    $healthyPeerCount = 0
    $quorumNeeded = 0
    if ($nodeReachable) {
        if ($etcdNodes.Count -eq 0) {
            throw "No node in this cluster carries the node-role.kubernetes.io/etcd label with an InternalIP, so there is no membership to reason about quorum against. Nodes: $(($clusterNodes | ForEach-Object { $_.Name }) -join ', '). This script will not switch off a control-plane node it cannot count."
        }

        $etcdHealth = Get-EtcdMemberHealth -Ssh $ssh -EtcdNodes $etcdNodes

        # Losing one member has to leave a majority of the whole membership
        # standing, which for the usual three is two. Note the membership,
        # not the survivors: etcd counts votes against the members it knows
        # about, so a cluster already down to two healthy of three is one
        # planned reboot away from having no quorum at all.
        $quorumNeeded = [math]::Floor($etcdNodes.Count / 2) + 1

        if ($etcdHealth.ProbeRan) {
            $healthyPeerCount = @($etcdHealth.Healthy | Where-Object { $_.Name -ne $hostname }).Count
        }
        else {
            # The certificates are not where k3s puts them, or this is not an
            # embedded-etcd cluster. Say so, and fall back to the weaker
            # signal rather than reporting health that was never measured.
            Write-Warning "etcd's own /health endpoint could not be reached on $IPAddress ($EtcdCertDir). Falling back to node readiness, which is a weaker statement: a node can be Ready while its etcd member is behind."
            $healthyPeerCount = @($etcdNodes | Where-Object { $_.Name -ne $hostname -and $_.Ready }).Count
        }

        if ($healthyPeerCount -lt $quorumNeeded) {
            $detail = if ($etcdHealth.ProbeRan) {
                ($etcdHealth.Members | ForEach-Object { "  - $($_.Name) ($($_.Address)): $(if ($_.Healthy) { 'healthy' } else { $_.Detail })" }) -join "`n"
            }
            else {
                ($etcdNodes | ForEach-Object { "  - $($_.Name) ($($_.InternalIp)): $(if ($_.Ready) { 'Ready' } else { 'NotReady' })" }) -join "`n"
            }
            throw "Refusing to take '$hostname' down: $healthyPeerCount of the other $($etcdNodes.Count - 1) etcd member(s) are healthy, and quorum for a $($etcdNodes.Count)-member cluster needs $quorumNeeded. Stopping this node now would leave the cluster without a majority and its API server read-only until it comes back. This refusal is not waived by -Force.`n$detail"
        }

        if (-not $targetNode.Ready) {
            if ($Force) {
                Write-Warning "Node '$hostname' is NotReady and -Force was passed. Proceeding."
            }
            else {
                throw "Node '$hostname' is NotReady. Find out why before draining and switching it off - a node that is already unwell is the worst one to start a storage migration on. Pass -Force if the reason is understood and this is the fix."
            }
        }
    }

    if ($nodeReachable) {
        Write-Host "Node:      $hostname, $(if ($targetNode.Ready) { 'Ready' } else { 'NotReady' })$(if ($targetNode.Unschedulable) { ', cordoned' })"
        Write-Host "Root:      $rootSource (partition $rootPartitionNumber of /dev/$rootDisk), $(Format-Size $guestUsedBytes) used of $(Format-Size $guestRootBytes)"
        Write-Host "Peer:      $($peer.Name) ($($peer.InternalIp)) - drain, watch and uncordon are issued from here"
        Write-Host "etcd:      $healthyPeerCount healthy peer(s) of $($etcdNodes.Count) member(s); quorum needs $quorumNeeded$(if ($etcdHealth -and -not $etcdHealth.ProbeRan) { ' (from node readiness, not from etcd)' })"
        Write-Host "growpart:  $(if ($growpartPresent) { 'present' } else { 'missing - cloud-guest-utils will be installed before the node is stopped' })"

        # The plan's step 1.1: record the number, so step 1.6 has something to
        # compare against. Read before anything is touched, and kept in the
        # migration record beside the new disk.
        $fsyncBefore = Get-EtcdFsyncP99 -Ssh $ssh
        if ($fsyncBefore -and $fsyncBefore.Count -gt 0) {
            Write-Host 'fsync p99: (etcd write-ahead log, before this run)'
            foreach ($reading in $fsyncBefore) {
                Write-Host "             $($reading.Instance)  $($reading.Millis) ms"
            }
        }
    }

    if ($PreflightOnly) {
        Write-Host ''
        if ($alreadyMigrated) {
            Write-Host "'$VMName' is already booted from a fixed $(Format-Size ([int64]$currentVhd.Size)) disk at $newDiskPath. A run without -PreflightOnly would change nothing." -ForegroundColor Green
        }
        else {
            Write-Host "Would drain '$hostname', stop '$VMName', resize '$sourcePath' to ${SizeGB} GiB, convert it $(if ($inPlace) { 'in place' } else { 'and relocate it' }) to a fixed disk at '$newDiskPath', repoint the VM, boot it, grow $rootSource, and uncordon."
            Write-Host "The source disk is left in place either way - deleting it is a separate run with -RemoveSourceDisk, after a $SoakDays-day soak."
        }
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here. Nothing on the host or the node was changed.' -ForegroundColor Yellow
        return
    }

    if ($mode -eq 'Move' -and $alreadyMigrated) {
        Write-Host ''
        Write-Host "'$VMName' is already booted from a fixed $(Format-Size ([int64]$currentVhd.Size)) disk at $newDiskPath. Nothing to do." -ForegroundColor Green
        return
    }

    # ================================================================== #
    #  -RemoveSourceDisk: plan step 4.1, and the only mode that deletes.
    # ================================================================== #
    if ($mode -eq 'RemoveSource') {
        Write-Stage 'Soak'

        $movedUtc = [datetime]::MinValue
        $movedRaw = [string](Get-Field $sidecar.Record 'movedUtc')
        if (-not [datetime]::TryParse($movedRaw, [ref]$movedUtc)) {
            throw "The migration record at '$sidecarPath' has no readable movedUtc ('$movedRaw'), so the soak cannot be checked. Pass -Force if the soak is known to have elapsed."
        }
        $soakedDays = [math]::Round(((Get-Date).ToUniversalTime() - $movedUtc.ToUniversalTime()).TotalDays, 1)
        Write-Host "Migrated:  $($movedUtc.ToString('yyyy-MM-dd HH:mm')) UTC - $soakedDays day(s) ago"

        if ($soakedDays -lt $SoakDays) {
            if ($Force) {
                Write-Warning "-Force: deleting the source disk after $soakedDays day(s) rather than the $SoakDays this plan asks for."
            }
            else {
                throw "'$VMName' was migrated $soakedDays day(s) ago and the soak is $SoakDays. Until this disk is deleted the rollback is one -Rollback away; after it, it is a restore. Wait, or pass -Force if you have a reason."
            }
        }

        if (-not $nodeReachable -or -not $targetNode.Ready) {
            throw "Node '$hostname' is not Ready. The source disk is the rollback path for a node that is not well, which is exactly what this one is - nothing is deleted while that is true."
        }
        if ($etcdHealth -and $etcdHealth.ProbeRan) {
            $unhealthy = @($etcdHealth.Members | Where-Object { -not $_.Healthy })
            if ($unhealthy.Count -gt 0) {
                throw "Not every etcd member is healthy ($(($unhealthy | ForEach-Object { $_.Name }) -join ', ')). The rollback path stays open until the cluster is whole."
            }
        }

        # Nothing else on this host may still be pointing at the file - and
        # a checkpoint's disks are not among Get-VM's, so both are asked.
        $stillAttached = @(Get-VM | Get-VMHardDiskDrive | Where-Object { Test-SamePath -A $_.Path -B $sourcePath })
        if ($stillAttached.Count -gt 0) {
            throw "'$sourcePath' is still attached to VM(s) $(($stillAttached | ForEach-Object { $_.VMName }) -join ', '). Refusing to delete a disk a VM references."
        }
        $snapshotRefs = @(Get-VM | Get-VMSnapshot | Get-VMHardDiskDrive | Where-Object { Test-SamePath -A $_.Path -B $sourcePath })
        if ($snapshotRefs.Count -gt 0) {
            throw "'$sourcePath' is referenced by $($snapshotRefs.Count) checkpoint(s) on this host. Deleting it would break them. Merge or remove those checkpoints first."
        }

        Write-Stage 'Delete'
        $freedBytes = (Get-Item $sourcePath).Length
        Remove-Item -Path $sourcePath -Force
        $actions.Add("deleted the source disk $sourcePath, freeing $(Format-Size $freedBytes)")
        Write-Host "Deleted $sourcePath ($(Format-Size $freedBytes) freed)."

        # The record is kept and amended rather than removed: it is the only
        # thing that will explain, a year from now, why this VM's disk is on
        # a different volume from every other file that host holds.
        $updated = $sidecar.Record | Select-Object *
        Add-Member -InputObject $updated -NotePropertyName 'sourceRemovedUtc' -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force
        Add-Member -InputObject $updated -NotePropertyName 'sourceRemovedFreedBytes' -NotePropertyValue $freedBytes -Force
        $updated | ConvertTo-Json -Depth 6 | Set-Content -Path $sidecarPath -Encoding UTF8
        Write-Host "Recorded in $sidecarPath."
    }
    else {
        # ============================================================== #
        #  Migrate, and -Rollback, which share everything but stage 6.
        # ============================================================== #
        $targetDiskPath = if ($mode -eq 'Rollback') { $sourcePath } else { $newDiskPath }

        if ($mode -eq 'Rollback') {
            Write-Warning "Rolling '$VMName' back to '$sourcePath'. That disk is a copy of this node taken at migration time, so anything the node has written since is only on '$newDiskPath'. For an etcd member that is fine - it rejoins and resyncs from the leader - but nothing outside etcd resyncs itself."
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Packages'
        # -------------------------------------------------------------- #

        if ($mode -eq 'Rollback' -or -not $nodeReachable) {
            Write-Host 'Skipped - nothing new is being grown.'
        }
        elseif ($growpartPresent) {
            Write-Host 'growpart: already present (cloud-guest-utils).'
        }
        else {
            # Before the node is stopped, deliberately: after stage 5 it has
            # no network, and after stage 7 the tool is needed within seconds
            # of the boot. `sudo env DEBIAN_FRONTEND=...` rather than a bare
            # prefix, because most sudoers policies reset the environment and
            # a dropped DEBIAN_FRONTEND is an apt that blocks on a prompt no
            # one will answer.
            Write-Host 'Installing cloud-guest-utils (growpart) ...'
            $install = Invoke-NodeSsh @ssh -ConnectTimeoutSec 600 -Command (
                'sudo env DEBIAN_FRONTEND=noninteractive apt-get update -qq && sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq cloud-guest-utils'
            )
            if ($install.ExitCode -ne 0) {
                throw "Installing cloud-guest-utils on $IPAddress failed (exit $($install.ExitCode)). Nothing has been changed on the host:`n$($install.StdOut)$($install.StdErr)"
            }
            $actions.Add('installed cloud-guest-utils')
            Write-Host '  installed.'
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Drain'
        # -------------------------------------------------------------- #

        if (-not $nodeReachable) {
            Write-Host "Skipped - $IPAddress is not answering, so there is nothing running on it to move."
        }
        else {
            Write-Host "Draining '$hostname' from $($peer.Name) ..."
            $drain = Invoke-NodeSsh @peerSsh -ConnectTimeoutSec 900 -Command (
                "sudo k3s kubectl drain $hostname --ignore-daemonsets --delete-emptydir-data --timeout=600s"
            )
            if ($drain.ExitCode -ne 0) {
                throw @"
Draining '$hostname' failed (exit $($drain.ExitCode)). The node is left cordoned; nothing on the host has been changed, and 'sudo k3s kubectl uncordon $hostname' from $($peer.Name) puts it back.

A drain that times out is usually a PodDisruptionBudget that cannot be satisfied - most often Longhorn's, when this node holds the only replica of an attached volume. That is worth knowing before switching the machine off, which is why it is a stop rather than a --force.

$($drain.StdOut)$($drain.StdErr)
"@
            }
            $actions.Add("drained and cordoned $hostname")
            Write-Host '  drained.'
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Stop'
        # -------------------------------------------------------------- #

        if ($vm.State -eq 'Off') {
            Write-Host "'$VMName' is already Off."
        }
        else {
            # Graceful. -Force here only means "do not prompt" - it is
            # Stop-VM -TurnOff that pulls the power, and that is not a
            # decision a script makes about an etcd member.
            Write-Host "Stopping '$VMName' ..."
            Stop-VM -Name $VMName -Force
            $stopDeadline = (Get-Date).AddMinutes(5)
            while ((Get-VM -Name $VMName).State -ne 'Off') {
                if ((Get-Date) -gt $stopDeadline) {
                    throw "'$VMName' has not reached Off after 5 minutes. Nothing has been converted and the node is cordoned. Watch the console with 'vmconnect localhost $VMName'; if it is genuinely hung, 'Stop-VM -Name $VMName -TurnOff' is the operator's call to make, not this script's."
                }
                Start-Sleep -Seconds 5
            }
            $actions.Add("stopped $VMName")
            Write-Host '  off.'
        }

        # --- the merge, here and deliberately not in preflight ---------- #
        #
        # Merging a checkpoint chain rewrites every block the .avhdx holds,
        # and on a host whose VM volume backs both the OS disk and the node's
        # 200GB Longhorn data disk that saturates the volume the guest is
        # living on. Done against a *running* node it starves the guest:
        # kubelet stalls, Longhorn's instance-manager is killed, its replicas
        # stop, volumes go degraded, and the rebuild that follows lands more
        # I/O on the same disk. That is not a hypothetical - it is what one
        # run of this script did to a node before the merge moved here.
        #
        # With the VM off there is no guest to starve. The node is already
        # drained and already down for the conversion that follows, so the
        # merge costs wall clock inside a window that was being spent anyway.
        #
        # A clean Stop-VM may have merged it already: Hyper-V removes an
        # automatic checkpoint when a VM shuts down cleanly. So the snapshot
        # list is re-read here rather than trusting preflight's, and what is
        # waited for is the chain being gone, however it went.
        $remainingSnapshots = @(Get-VMSnapshot -VMName $VMName -ErrorAction SilentlyContinue)
        if ($remainingSnapshots.Count -gt 0) {
            Write-Host "Merging $($remainingSnapshots.Count) checkpoint(s), with '$VMName' off ..."
            foreach ($snapshot in $remainingSnapshots) {
                Write-Host "  removing '$($snapshot.Name)' [$($snapshot.SnapshotType)], taken $($snapshot.CreationTime.ToString('yyyy-MM-dd HH:mm')) ..."
                Remove-VMSnapshot -VMSnapshot $snapshot
            }
        }
        elseif ($chainDepth -gt 0) {
            Write-Host 'The clean shutdown already removed the checkpoint; waiting for its merge to finish ...'
        }

        if ($chainDepth -gt 0 -or $remainingSnapshots.Count -gt 0) {
            # Three signals, and the third is the authoritative one: the
            # checkpoint can disappear from Get-VMSnapshot and the status can
            # still read normally in the moment between Hyper-V accepting the
            # request and starting the merge. What has to hold before
            # Convert-VHD runs is that no .avhdx is attached at all.
            $mergeDeadline = (Get-Date).AddMinutes(90)
            Start-Sleep -Seconds 5
            while ($true) {
                $vm = Get-VM -Name $VMName
                $left = @(Get-VMSnapshot -VMName $VMName -ErrorAction SilentlyContinue)
                $merging = [string]$vm.Status -match 'Merg'
                $chained = $true
                try { $chained = @(Get-VMHardDiskDrive -VMName $VMName | Where-Object { $_.Path -like '*.avhdx' }).Count -gt 0 } catch { }
                if ($left.Count -eq 0 -and -not $merging -and -not $chained) { break }
                if ((Get-Date) -gt $mergeDeadline) {
                    throw "'$VMName' is still merging its checkpoint chain after 90 minutes (status: $($vm.Status), $($left.Count) checkpoint(s) left, differencing disk attached: $chained). The VM is off and the node is drained and cordoned; nothing has been converted. Let the merge finish, then re-run."
                }
                Write-Host "  $($vm.Status) ..."
                Start-Sleep -Seconds 15
            }
            $actions.Add('merged the checkpoint chain with the VM off')
            Write-Host '  merged.'

            # Re-read, and check it against what preflight decided. The whole
            # of the Disk stage below acts on $sourcePath, and preflight
            # predicted that path by walking a chain that no longer exists.
            $currentDrive = Get-VmOsDisk -VMName $VMName
            $currentVhd = Get-VHD -Path $currentDrive.Path
            if ($currentVhd.VhdType -eq 'Differencing') {
                throw "'$($currentDrive.Path)' is still a differencing disk after the merge. Nothing has been converted; the VM is off and its disks are as they were."
            }
            if (-not (Test-SamePath -A $currentDrive.Path -B $baseDiskPath)) {
                throw "After the merge, '$VMName' boots from '$($currentDrive.Path)', but preflight resolved the disk under the chain to '$baseDiskPath'. Refusing to act on a file this run did not predict. Nothing has been converted."
            }
            Write-Host "OS disk:   $($currentDrive.Path) - $($currentVhd.VhdType), $(Format-Size ([int64]$currentVhd.Size)) virtual, $(Format-Size ([int64]$currentVhd.FileSize)) on disk"
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Disk'
        # -------------------------------------------------------------- #

        if ($mode -eq 'Rollback') {
            Write-Host "Skipped - '$sourcePath' is the disk this VM already had."
        }
        else {
            $targetBytes = [int64]$SizeGB * 1GB

            # Instant, and the cheapest thing here to undo: it rewrites a
            # header field. Done on the source rather than after the convert
            # so that one Convert-VHD pass does both the type change and the
            # relocation, which is the difference between writing the disk
            # twice and writing it once.
            if ([int64]$currentVhd.Size -lt $targetBytes) {
                Write-Host "Resizing '$sourcePath' $(Format-Size ([int64]$currentVhd.Size)) -> $(Format-Size $targetBytes) (virtual size only) ..."
                Resize-VHD -Path $sourcePath -SizeBytes $targetBytes
                $actions.Add("resized $sourcePath to $(Format-Size $targetBytes) virtual")
                Write-Host '  resized.'
            }
            else {
                Write-Host "'$sourcePath' is already $(Format-Size ([int64]$currentVhd.Size)) virtual - not resized."
            }

            $newDiskDir = Split-Path -Parent $newDiskPath
            if (-not (Test-Path $newDiskDir -PathType Container)) {
                New-Item -ItemType Directory -Path $newDiskDir -Force | Out-Null
            }

            # The long step, and the only one that is measured in tens of
            # minutes: a fixed disk is written in full, so this is $SizeGB of
            # zeroes plus the data, onto the destination volume. One pass
            # converts and relocates; the source is read, never written.
            Write-Host "Converting to a fixed disk at '$newDiskPath' - this writes $(Format-Size $targetBytes) and is the long step ..."
            $convertStarted = Get-Date
            Convert-VHD -Path $sourcePath -DestinationPath $newDiskPath -VHDType Fixed
            $convertMinutes = [math]::Round(((Get-Date) - $convertStarted).TotalMinutes, 1)

            $newVhd = Get-VHD -Path $newDiskPath
            if ($newVhd.VhdType -ne 'Fixed') {
                throw "'$newDiskPath' came out as $($newVhd.VhdType), not Fixed. The VM has not been repointed and still boots from '$sourcePath'."
            }
            if ([int64]$newVhd.Size -ne $targetBytes) {
                throw "'$newDiskPath' is $(Format-Size ([int64]$newVhd.Size)), not the $(Format-Size $targetBytes) asked for. The VM has not been repointed and still boots from '$sourcePath'."
            }
            $actions.Add("converted to a fixed $(Format-Size $targetBytes) disk at $newDiskPath in ${convertMinutes} min")
            Write-Host "  converted in ${convertMinutes} min: $($newVhd.VhdType), $(Format-Size ([int64]$newVhd.Size)) virtual, $(Format-Size ([int64]$newVhd.FileSize)) on disk."
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Repoint'
        # -------------------------------------------------------------- #

        # Same controller slot, so the guest sees the same disk in the same
        # place and /dev/sda stays /dev/sda. Only the file behind it changes.
        Write-Host "Repointing $($currentDrive.ControllerType) $($currentDrive.ControllerNumber):$($currentDrive.ControllerLocation) at '$targetDiskPath' ..."
        Set-VMHardDiskDrive -VMName $VMName `
            -ControllerType $currentDrive.ControllerType `
            -ControllerNumber $currentDrive.ControllerNumber `
            -ControllerLocation $currentDrive.ControllerLocation `
            -Path $targetDiskPath

        # Reasserted rather than assumed. The firmware boot entry is a
        # reference to a drive, and Get-VmOsDisk resolves it back to a path -
        # so leaving it stale would mean the next run of this script cannot
        # tell which disk the VM boots from, on a VM whose disks are now on
        # two different volumes.
        $newDrive = @(Get-VMHardDiskDrive -VMName $VMName | Where-Object { $_.Path -eq $targetDiskPath })
        if ($newDrive.Count -ne 1) {
            throw "After repointing, '$VMName' does not have exactly one disk at '$targetDiskPath'. Inspect it with Get-VMHardDiskDrive before starting it."
        }
        Set-VMFirmware -VMName $VMName -FirstBootDevice $newDrive[0]
        $actions.Add("repointed $VMName at $targetDiskPath")
        Write-Host '  repointed, and set as the first boot device.'

        if ($mode -eq 'Rollback') {
            # Retired rather than deleted: the file still records what was
            # done and when, and renaming it is what stops -RemoveSourceDisk
            # from later deleting a disk the VM has been put back onto.
            $retiredPath = [IO.Path]::ChangeExtension($sidecarPath, $null).TrimEnd('.') + '.rolledback.json'
            $rolled = $sidecar.Record | Select-Object *
            Add-Member -InputObject $rolled -NotePropertyName 'rolledBackUtc' -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force
            $rolled | ConvertTo-Json -Depth 6 | Set-Content -Path $retiredPath -Encoding UTF8
            Remove-Item -Path $sidecarPath -Force
            $actions.Add("retired the migration record to $retiredPath")
            Write-Host "  migration record retired to $retiredPath."
        }
        else {
            # Written before the VM is started, deliberately. A node that does
            # not boot is precisely when the rollback path is needed, and a
            # record written after a successful boot would not exist then.
            $sidecarPath = Join-Path (Split-Path -Parent $newDiskPath) $SidecarFileName
            $record = [ordered]@{
                script               = 'scripts/hyperv/Move-NodeOsDisk.ps1'
                plan                 = 'docs/plans/node-storage.md Phase 1'
                vmName               = $VMName
                hyperVHost           = $env:COMPUTERNAME
                movedUtc             = (Get-Date).ToUniversalTime().ToString('o')
                sourcePath           = $sourcePath
                sourceVhdType        = [string]$currentVhd.VhdType
                sourceSizeBytes      = [int64]$currentVhd.Size
                destinationPath      = $newDiskPath
                destinationVhdType   = 'Fixed'
                convertedInPlace     = [bool]$inPlace
                destinationSizeBytes = [int64]$SizeGB * 1GB
                controllerType       = [string]$currentDrive.ControllerType
                controllerNumber     = [int]$currentDrive.ControllerNumber
                controllerLocation   = [int]$currentDrive.ControllerLocation
                fsyncP99BeforeMs     = if ($fsyncBefore) { @($fsyncBefore | ForEach-Object { [ordered]@{ instance = $_.Instance; millis = $_.Millis } }) } else { @() }
                rollbackWith         = ".\Move-NodeOsDisk.ps1 -VMName $VMName -IPAddress $IPAddress -Rollback"
                deleteSourceWith     = ".\Move-NodeOsDisk.ps1 -VMName $VMName -IPAddress $IPAddress -RemoveSourceDisk"
            }
            $record | ConvertTo-Json -Depth 6 | Set-Content -Path $sidecarPath -Encoding UTF8
            Write-Host "  migration recorded in $sidecarPath (the source disk is left in place; this names it)."
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Start'
        # -------------------------------------------------------------- #

        Write-Host "Starting '$VMName' ..."
        try {
            Start-VM -Name $VMName
        }
        catch {
            throw "Start-VM failed for '$VMName' after repointing it at '$targetDiskPath': $($_.Exception.Message)`n`nIf that reads as an access or permission failure, the new file is missing the VM's own SID from its ACL - Hyper-V normally adds it during Set-VMHardDiskDrive. Grant it with:`n  icacls '$targetDiskPath' /grant 'NT VIRTUAL MACHINE\$((Get-VM -Name $VMName).Id)':(F)"
        }
        $actions.Add("started $VMName")

        Wait-NodeSshReady -Ssh $ssh -ExpectedHostname $hostname -TimeoutMinutes 15
        Write-Host "  '$hostname' is answering SSH."

        # -------------------------------------------------------------- #
        Write-Stage 'Grow'
        # -------------------------------------------------------------- #

        $grownBytes = [int64]0
        if ($mode -eq 'Rollback') {
            Write-Host 'Skipped - the disk this VM was put back on has the layout it always had.'
        }
        else {
            # The partition table cooperates because the root partition is the
            # last one by LBA, which stage 2 proved on the live node. Both of
            # these are online operations on a mounted filesystem.
            Write-Host "Growing $rootSource into the new $(Format-Size ([int64]$SizeGB * 1GB)) ..."
            $growScript = @(
                "sudo growpart /dev/$rootDisk $rootPartitionNumber > /tmp/aerie-growpart.log 2>&1"
                'echo rc=$?'
                'cat /tmp/aerie-growpart.log'
                'rm -f /tmp/aerie-growpart.log'
            ) -join '; '
            $grow = Invoke-NodeSsh @ssh -Command $growScript -ConnectTimeoutSec 120
            $growRc = 1
            if ($grow.StdOut -match 'rc=(\d+)') { $growRc = [int]$Matches[1] }
            # growpart exits non-zero with NOCHANGE when the partition already
            # fills the disk, which is the ordinary result of a re-run and not
            # an error. Anything else is.
            if ($growRc -ne 0 -and $grow.StdOut -notmatch 'NOCHANGE') {
                throw "growpart /dev/$rootDisk $rootPartitionNumber failed on $IPAddress (rc=$growRc). The node is up and cordoned, booted from the new disk; its filesystem is simply still the old size.`n$($grow.StdOut)$($grow.StdErr)"
            }
            Write-Host "  $(if ($growRc -eq 0) { 'partition grown' } else { 'partition already filled the disk' })."

            # Debian's genericcloud root filesystem is ext4, but the tool is
            # chosen from what the node actually reports rather than from what
            # the image is expected to be.
            $rootFsEntry = @($devices | Where-Object { $_.Path -eq $rootSource })
            $rootFsType = if ($rootFsEntry.Count -eq 1) { $rootFsEntry[0].FsType } else { '' }
            $resizeCommand = switch -Regex ($rootFsType) {
                '^ext[234]$' { "sudo resize2fs $rootSource" }
                '^xfs$'      { 'sudo xfs_growfs /' }
                default      { $null }
            }
            if (-not $resizeCommand) {
                throw "/ on $IPAddress is '$rootFsType', which this script has no grow command for. The partition is now the full $(Format-Size ([int64]$SizeGB * 1GB)); grow the filesystem into it by hand."
            }

            $resize = Invoke-NodeSsh @ssh -Command $resizeCommand -ConnectTimeoutSec 300
            if ($resize.ExitCode -ne 0) {
                throw "Growing the $rootFsType filesystem on $IPAddress failed (exit $($resize.ExitCode)). The partition is the full size; the filesystem is not.`n$($resize.StdOut)$($resize.StdErr)"
            }
            $actions.Add("grew $rootSource and its $rootFsType filesystem to $(Format-Size ([int64]$SizeGB * 1GB))")
            Write-Host '  filesystem grown.'
        }

        # -------------------------------------------------------------- #
        Write-Stage 'Verify'
        # -------------------------------------------------------------- #

        # The one path that reaches here with nothing learned in stage 2: a
        # -Rollback against a node that was already off the network, which is
        # the case that mode exists for. It is answering now, so the facts
        # that stage 2 would have collected are collected here instead, and
        # only then is anything asserted about them.
        if (-not $nodeReachable) {
            $late = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (@(
                    'echo ''--- root'''
                    'findmnt -n -o SOURCE /'
                    'echo ''--- nodes'''
                    'sudo k3s kubectl get nodes -o json 2>/dev/null || echo {}'
                    'echo ''--- end'''
                ) -join '; ')
            if ($late.ExitCode -ne 0) {
                throw "'$hostname' answered SSH but the follow-up probe failed (exit $($late.ExitCode)):`n$($late.StdOut)$($late.StdErr)"
            }
            $rootSource = (Get-ProbeSection -Output $late.StdOut -Name 'root').Trim()
            $clusterNodes = @(ConvertTo-ClusterNodeList -Json (Get-ProbeSection -Output $late.StdOut -Name 'nodes'))
            $etcdNodes = @($clusterNodes | Where-Object { $_.IsEtcdMember -and $_.InternalIp })
            $latePeer = @($clusterNodes | Where-Object { $_.Name -ne $hostname -and $_.Ready -and $_.InternalIp } | Sort-Object Name)
            if ($latePeer.Count -gt 0) {
                $peer = $latePeer[0]
                $peerSsh = @{ IPAddress = $peer.InternalIp; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }
            }
            Write-Host "Node is back: / on $rootSource, $($clusterNodes.Count) node(s) visible."
        }

        # Re-read from the node rather than trusting what was just done.
        $verify = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (@(
                'echo ''--- df'''
                'df -B1 --output=size,used,avail,pcent / | tail -n 1'
                'echo ''--- root'''
                'findmnt -n -o SOURCE /'
                'echo ''--- end'''
            ) -join '; ')
        if ($verify.ExitCode -ne 0) {
            throw "Verification on $IPAddress failed (exit $($verify.ExitCode)):`n$($verify.StdOut)$($verify.StdErr)"
        }

        $verifyRoot = (Get-ProbeSection -Output $verify.StdOut -Name 'root').Trim()
        if ($verifyRoot -ne $rootSource) {
            throw "/ on $IPAddress is now on $verifyRoot rather than $rootSource. The node booted, but not from the device this run expected."
        }
        $verifyFields = @((Get-ProbeSection -Output $verify.StdOut -Name 'df').Trim() -split '\s+' | Where-Object { $_ })
        if ($verifyFields.Count -ge 3) {
            [void][int64]::TryParse($verifyFields[0], [ref]$grownBytes)
        }

        if ($mode -eq 'Move') {
            # ext4 metadata - the journal, inode tables, group descriptors -
            # is a few percent of any disk, so the floor is what is checked
            # rather than the exact number. What this really asserts is that
            # the filesystem is the new size and not the old one.
            $floorBytes = ([int64]$SizeGB * 1GB) * 0.90
            if ($grownBytes -lt $floorBytes) {
                throw "/ on $IPAddress is $(Format-Size $grownBytes) after the grow, well under the $(Format-Size ([int64]$SizeGB * 1GB)) disk it now sits on. The node is up and cordoned; find out why before uncordoning it."
            }
            Write-Host "Root fs:   $(Format-Size $grownBytes), up from $(Format-Size $guestRootBytes)"
        }

        # Asked of a peer where there is one, because a node's own kubelet can
        # report Ready while its etcd member has not rejoined. Where there is
        # not - a rollback of the last node standing - its own answer is the
        # only one available, and is labelled as the weaker statement it is.
        $watcherSsh = if ($peerSsh) { $peerSsh } else { $ssh }
        $watcherName = if ($peer) { $peer.Name } else { "$hostname itself (no peer available - a weaker check)" }
        Write-Host "Waiting for '$hostname' to report Ready to $watcherName ..."
        $readyNode = Wait-ClusterNodeReady -Ssh $watcherSsh -NodeName $hostname -TimeoutMinutes 15
        Write-Host '  Ready.'

        # A node can be Ready while its etcd member is still catching up, and
        # the whole point of this change is etcd. Asked of the target itself
        # now that it is back, so it is that member's own answer.
        $healthAfter = Get-EtcdMemberHealth -Ssh $ssh -EtcdNodes $etcdNodes
        if ($healthAfter.ProbeRan -and $etcdNodes.Count -gt 0) {
            $stillDown = @($healthAfter.Members | Where-Object { -not $_.Healthy })
            $deadline = (Get-Date).AddMinutes(5)
            while ($stillDown.Count -gt 0 -and (Get-Date) -lt $deadline) {
                Start-Sleep -Seconds 10
                $healthAfter = Get-EtcdMemberHealth -Ssh $ssh -EtcdNodes $etcdNodes
                $stillDown = @($healthAfter.Members | Where-Object { -not $_.Healthy })
            }
            if ($stillDown.Count -gt 0) {
                throw "etcd member(s) $(($stillDown | ForEach-Object { $_.Name }) -join ', ') are still not healthy 5 minutes after '$hostname' came back. The node is up and cordoned - it is not carrying workload - but do not run this against another node until the cluster is whole."
            }
            Write-Host "etcd:      all $($healthAfter.Members.Count) member(s) healthy."
        }

        if ($readyNode.Unschedulable) {
            Write-Host "Uncordoning '$hostname' ..."
            [void](Invoke-NodeKubectl -Ssh $watcherSsh -Arguments "uncordon $hostname")
            $actions.Add("uncordoned $hostname")
            Write-Host '  uncordoned.'
        }
        else {
            Write-Host "'$hostname' is already schedulable."
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    $headline = switch ($mode) {
        'Move'         { "'$VMName' now boots from a fixed $(Format-Size ([int64]$SizeGB * 1GB)) disk on $((Get-DestinationVolumeFree -Path $newDiskPath).DriveLetter):$(if ($inPlace) { ' (converted in place)' }) - done in ${elapsed} min." }
        'Rollback'     { "'$VMName' is back on '$sourcePath' - done in ${elapsed} min." }
        'RemoveSource' { "'$VMName''s source disk is gone - done in ${elapsed} min." }
    }
    Write-Host $headline -ForegroundColor Green

    if ($actions.Count -eq 0) {
        Write-Host 'Nothing needed changing.'
    }
    else {
        Write-Host 'Changed:'
        foreach ($action in $actions) { Write-Host "  - $action" }
    }

    if ($mode -eq 'Move') {
        Write-Host ''
        Write-Host "Next, and neither is automatic:"
        Write-Host "  1. The gate. p99 etcd wal fsync on this node under 10 ms, sustained over an hour,"
        Write-Host "     before the next node is touched. Before this run it was:"
        if ($fsyncBefore -and $fsyncBefore.Count -gt 0) {
            foreach ($reading in $fsyncBefore) { Write-Host "       $($reading.Instance)  $($reading.Millis) ms" }
        }
        else {
            Write-Host '       (not read - record it by hand)'
        }
        Write-Host "  2. The source disk is still at '$sourcePath'. It is the rollback path for the next"
        Write-Host "     $SoakDays days; -RemoveSourceDisk deletes it after that, and -Rollback undoes this before."
    }

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $summary = New-Object Collections.Generic.List[string]
        $summary.Add("## OS disk $(switch ($mode) { 'Move' { 'moved' } 'Rollback' { 'rolled back' } 'RemoveSource' { 'source deleted' } }) for $tick$VMName$tick")
        $summary.Add('')
        $summary.Add('| | |')
        $summary.Add('|---|---|')
        $summary.Add("| Host | $tick$env:COMPUTERNAME$tick |")
        $summary.Add("| Node | $tick$hostname$tick ($tick$IPAddress$tick) |")
        switch ($mode) {
            'Move' {
                $summary.Add("| Operation | $(if ($inPlace) { 'converted in place - the destination volume is the one it was already on' } else { 'moved to another volume, and converted' }) |")
                $summary.Add("| Was | $tick$sourcePath$tick - $($currentVhd.VhdType), $(Format-Size ([int64]$currentVhd.Size)) |")
                $summary.Add("| Now | $tick$newDiskPath$tick - Fixed, $(Format-Size ([int64]$SizeGB * 1GB)) |")
                $summary.Add("| Root filesystem | $(Format-Size $guestRootBytes) -> $(Format-Size $grownBytes) |")
                $summary.Add("| Source disk | left in place for the $SoakDays-day soak |")
            }
            'Rollback' {
                $summary.Add("| Was | $tick$newDiskPath$tick - $($currentVhd.VhdType), $(Format-Size ([int64]$currentVhd.Size)) |")
                $summary.Add("| Now | $tick$sourcePath$tick - the disk this node had before the migration |")
                $summary.Add('| Migrated disk | left in place for inspection |')
            }
            'RemoveSource' {
                $summary.Add("| Booted from | $tick$newDiskPath$tick - $($currentVhd.VhdType), $(Format-Size ([int64]$currentVhd.Size)) |")
                $summary.Add("| Deleted | $tick$sourcePath$tick |")
                $summary.Add('| Rollback | no longer available - this was it |')
            }
        }
        if ($peer) { $summary.Add("| Drained from | $tick$($peer.Name)$tick |") }
        $summary.Add("| Elapsed | ${elapsed} min |")
        $summary.Add('')
        if ($mode -eq 'Move' -and $fsyncBefore -and $fsyncBefore.Count -gt 0) {
            $summary.Add('### etcd wal fsync p99 before this run')
            $summary.Add('')
            $summary.Add('| member | p99 |')
            $summary.Add('|---|---|')
            foreach ($reading in $fsyncBefore) { $summary.Add("| $tick$($reading.Instance)$tick | $($reading.Millis) ms |") }
            $summary.Add('')
            $summary.Add("The plan's gate is **under 10 ms sustained over an hour** on this node before the next one is touched.")
            $summary.Add('')
        }
        $summary.Add("**Changed:** $(if ($actions.Count -eq 0) { 'nothing' } else { ($actions -join '; ') })")
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n")
    }
}
finally {
    if ($tempKeyFile -and (Test-Path $tempKeyFile)) { Remove-Item -Path $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile -and (Test-Path $knownHostsFile)) { Remove-Item -Path $knownHostsFile -Force -ErrorAction SilentlyContinue }
}
