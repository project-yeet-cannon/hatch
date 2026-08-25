<#
.SYNOPSIS
    Prepares one k3s node's *bulk* disk over SSH - the dedicated disk the photo
    library lives on - and registers it with Longhorn under the `bulk` tag so
    nothing else can ever be scheduled onto it.

.DESCRIPTION
    The photos plan (docs/plans/immich.md) Phase 1, steps 1.2 and 1.3. Sibling
    of Initialize-NodeStorage.ps1 and deliberately the same shape: the same SSH
    mechanics from ..\hyperv\lib\AerieSsh.ps1, the same probe-once-then-decide
    structure, the same identify-the-disk-by-shape trick, and the same two hard
    rules that no in-use disk and no root disk is ever a candidate.

    Three things make it a separate script rather than a parameter on its
    sibling:

      1. It runs *after* Longhorn, not before. Its last stage talks to the
         Kubernetes API, which does not exist when Initialize-NodeStorage runs.
      2. It runs on **one** node, not every node. The library is one volume
         with one replica; the disk exists on exactly one machine.
      3. The filesystem is on **LVM**, not on the bare disk. Its sibling's disk
         is a fixed 200GB VHDX that will never change size; this one is sized
         against whatever free space the chosen host actually had, and growing
         it later must not mean rebuilding it.

    That third point is the whole reason for the extra layer, so it is worth
    stating plainly: with vg_bulk holding one physical volume today, adding a
    second disk later is `-ExtendVolumeGroup`, which pvcreates it, vgextends,
    lvextends into the new free space and grows the ext4 online. Without LVM
    the same operation is: create a bigger disk, copy a terabyte of replica
    data, and re-register. The layer costs one indirection and buys a growth
    path that never takes the volume offline.

    Stages:
      1. Preflight  - SSH key resolves, the OpenSSH client is present, and the
                      node answers port 22.
      2. Inspect    - one read-only probe collects the node's identity, its
                      whole block-device tree, the Longhorn data mount, the
                      LVM state, and the node's own Longhorn CR. Everything
                      below is decided from that snapshot and printed before
                      it is acted on. -PreflightOnly stops here.
      3. Packages   - lvm2, if it isn't there. The LVM state is re-read
                      afterwards, because a probe taken before lvm2 existed
                      could not have seen a volume group.
      4. Disk       - pvcreate / vgcreate / lvcreate / mkfs.ext4, or the
                      extend path, then the fstab entry keyed by UUID and the
                      mount at /var/lib/longhorn-bulk.
      5. Longhorn   - patches the node's node.longhorn.io CR to add the disk
                      with tags: ["bulk"], and labels the Kubernetes Node
                      storage.aerie/bulk=true. **The tag is the entire safety
                      mechanism.** Without it Longhorn treats a large empty
                      disk as general capacity and will place Prometheus and
                      OpenSearch replicas on it. The label is what steers the
                      consumer pod here - see that stage for why the tag alone
                      cannot.
      6. Verify     - re-reads the mount, the fstab entry, the systemd mount
                      unit, the node label, and waits for Longhorn to report
                      the disk Ready and Schedulable with the tag it was
                      given.

    Idempotent: a re-run against a prepared node finds the volume group,
    reconciles the fstab entry, leaves the Longhorn CR alone if it already
    matches, and verifies. Nothing is formatted twice, and -Force is required
    before anything that already holds data is written to.

    **No node name from this script ever reaches the repo.** Which node holds
    the disk is an argument here and a fact about where an operator physically
    attached hardware. What the repo names is the disk tag and the node label
    this script writes, both of them derived from that one fact; the cluster
    derives placement from those. That is the point of both, and docs/ethos.md
    is why.

.PARAMETER BulkDiskSizeGB
    The size the bulk disk was created at, and what makes it identifiable
    without naming a device: the right disk is the unpartitioned, empty one of
    about this size. Sizes are binary, as in New-AerieVM.ps1, so 1200 means
    1200 GiB.

    The default is 1200 and not the 2000 the plan first assumed, because no
    host had 2 TB of contiguous free space. 1200 GiB holds the ~800 GB library
    plus its ~20% of derivatives with room for several years of growth, and
    leaves headroom on the host volume so a dynamic VHDX cannot fill the drive
    it shares with a node's 200GB Longhorn disk. Growth past it is
    -ExtendVolumeGroup, not a resize.

.PARAMETER ExtendVolumeGroup
    Grows an existing bulk volume onto a second disk: pvcreate, vgextend,
    lvextend into all free space, resize2fs online. Requires the volume group
    to exist already and exactly one new empty disk of about -BulkDiskSizeGB
    to be attached - which in this mode describes the *new* disk, not the
    original one.

    A switch rather than automatic behaviour on purpose. An ordinary re-run
    finding an unexpected empty disk should report it, not absorb it.

.PARAMETER Force
    DESTRUCTIVE. Allows a candidate disk that already carries a partition
    table or a filesystem to be wiped. Without it such a disk is refused,
    because "an unpartitioned disk of the expected size" is the only evidence
    this script has that it found the bulk disk and not something that
    matters. -Force never widens the two hard rules: a disk with anything
    mounted from it, anywhere in its tree, is never a candidate, and neither
    is the disk holding the root filesystem.

.EXAMPLE
    .\Add-BulkDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.22 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Look at what it would do, without touching the node
    .\Add-BulkDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.22 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519 -PreflightOnly

.EXAMPLE
    # Years later: a second disk was attached to the same node
    .\Add-BulkDisk.ps1 -VMName aerie-node-N -IPAddress 10.0.0.22 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519 -BulkDiskSizeGB 900 -ExtendVolumeGroup
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$')]
    [string]$VMName,

    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [ValidateRange(1, 65536)]
    [int]$BulkDiskSizeGB = 1200,

    # Same reasoning as Initialize-NodeStorage.ps1: a VHDX is exactly the size
    # it was created at, but passthrough of a whole physical disk is supported
    # and a "1.5TB" disk is never 1.5 * 2^40 bytes.
    [ValidateRange(0, 50)]
    [int]$SizeTolerancePercent = 5,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [switch]$ExtendVolumeGroup,

    [switch]$Force,

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')
. (Join-Path $PSScriptRoot 'lib\AerieNodeDisk.ps1')

# None of these five are parameters, and for the same reason
# Initialize-NodeStorage.ps1 fixes its mount point: each one has to agree with
# something set elsewhere by different means, and a value that varies per run
# is a silent mismatch rather than an error.
#
#   $MountPoint      - what the Longhorn node CR's disk entry points at, and
#                      what an operator reading `df` on the node will look for.
#   $VolumeGroup     - named in every extend, every rescue and every runbook
#                      line about this disk from here on.
#   $LogicalVolume   -   "
#   $FilesystemLabel - the second way this script recognises its own work,
#                      surviving a wiped fstab and a reordered controller.
#   $DiskTag         - the same literal as diskSelector: "bulk" in
#                      deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml.
#                      These two agreeing is half the scheduling contract.
#   $NodeLabel       - the other half, and the same literal as the
#                      nodeSelector on Immich's server pod in
#                      deploy/cluster/photos/app/helmrelease.yaml. See the
#                      Longhorn stage for why a disk tag alone is not enough.
$MountPoint = '/var/lib/longhorn-bulk'
$VolumeGroup = 'vg_bulk'
$LogicalVolume = 'lv_bulk'
$FilesystemLabel = 'longhorn-bulk'
$DiskTag = 'bulk'
$NodeLabelKey = 'storage.aerie/bulk'
$NodeLabelValue = 'true'

# The key of the disk entry inside the node CR's spec.disks map. Longhorn
# generates a random one for the disk the chart registers (default-disk-<hex>);
# this one is named, so the merge patch below is idempotent and an operator
# reading the CR can tell which entry this script owns.
$LonghornDiskName = 'bulk-disk'

# Longhorn v1 volumes attach over iSCSI and need open-iscsi, but that is
# Initialize-NodeStorage.ps1's job and this script asserts it rather than
# repeating it. lvm2 is the one package this step adds.
$RequiredPackages = @('lvm2')

function Get-NodeLvmState {
    <#
    .SYNOPSIS
        Reads the node's physical volumes, volume groups and logical volumes.

    .DESCRIPTION
        Its own function because it is read twice: once in the inspect probe,
        and again after the packages stage if lvm2 had to be installed. A
        probe taken before lvm2 existed reports no volume groups, which is
        true but not for the reason the caller would infer from it.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Ssh
    )

    $lvmScript = @(
        'echo ''--- pvs'''
        'command -v pvs >/dev/null 2>&1 && sudo pvs --noheadings --nosuffix --units b -o pv_name,vg_name 2>/dev/null || true'
        'echo ''--- vgs'''
        'command -v vgs >/dev/null 2>&1 && sudo vgs --noheadings --nosuffix --units b -o vg_name,pv_count,vg_size,vg_free 2>/dev/null || true'
        'echo ''--- lvs'''
        'command -v lvs >/dev/null 2>&1 && sudo lvs --noheadings --nosuffix --units b -o vg_name,lv_name,lv_size,lv_path 2>/dev/null || true'
        'echo ''--- end'''
    ) -join '; '

    $result = Invoke-NodeSsh @Ssh -Command $lvmScript -ConnectTimeoutSec 20
    if ($result.ExitCode -ne 0) {
        throw "Reading the LVM state failed (exit $($result.ExitCode)):`n$($result.StdOut)$($result.StdErr)"
    }

    # Named columns rather than positional arrays, and not only for
    # readability: a pipeline that emits one array, wrapped in @(), is that
    # array rather than a one-element list of it, so a single volume group
    # would arrive as four strings. Objects do not have that failure mode.
    $rows = {
        param([string]$Section, [string[]]$Columns)
        $parsed = New-Object Collections.Generic.List[psobject]
        foreach ($line in ((Get-ProbeSection -Output $result.StdOut -Name $Section) -split "`n")) {
            $fields = @($line.Trim() -split '\s+' | Where-Object { $_ })
            if ($fields.Count -lt $Columns.Count) { continue }
            $row = [ordered]@{}
            for ($i = 0; $i -lt $Columns.Count; $i++) { $row[$Columns[$i]] = $fields[$i] }
            $parsed.Add([pscustomobject]$row)
        }
        return $parsed
    }

    # @() around each call, because a scriptblock returning an empty
    # List[psobject] returns nothing at all - and $null.Count is a terminating
    # error under Set-StrictMode, on the one path that matters most: a node
    # with no volume groups yet, which is every node the first time.
    return [pscustomobject]@{
        Pvs = @(& $rows 'pvs' @('PvName', 'VgName'))
        Vgs = @(& $rows 'vgs' @('VgName', 'PvCount', 'Size', 'Free'))
        Lvs = @(& $rows 'lvs' @('VgName', 'LvName', 'Size', 'Path'))
    }
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$expectedBytes = [int64]$BulkDiskSizeGB * 1GB
$hostname = $VMName.ToLowerInvariant()
$lvPath = "/dev/$VolumeGroup/$LogicalVolume"
$actions = New-Object Collections.Generic.List[string]

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

    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        $failures.Add("$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right.")
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed for '$VMName' ($IPAddress) with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Node:      $VMName ($IPAddress)"
    Write-Host "Bulk disk: ~${BulkDiskSizeGB}GB ($(Format-Size $expectedBytes)), +/-${SizeTolerancePercent}%"
    Write-Host "Mount:     $MountPoint, ext4 on $lvPath, label '$FilesystemLabel', by UUID in /etc/fstab"
    Write-Host "Longhorn:  disk '$LonghornDiskName' on node '$hostname', tags [$DiskTag]"
    Write-Host "Label:     node/$hostname $NodeLabelKey=$NodeLabelValue"
    if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    if ($ExtendVolumeGroup) { Write-Warning "-ExtendVolumeGroup: a new empty disk of about $(Format-Size $expectedBytes) will be absorbed into $VolumeGroup and the filesystem grown onto it." }
    if ($Force) { Write-Warning '-Force: a candidate disk that already holds a partition table or a filesystem will be wiped.' }
    Write-Host 'Preflight OK.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-bulk-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Inspect'
    # ---------------------------------------------------------------- #

    # Single quotes only, and no double quotes anywhere: Invoke-NodeSsh refuses
    # a command containing one, because Windows PowerShell 5.1 would let
    # ssh.exe strip it and run a subtly different script on the node.
    $probeScript = @(
        'echo ''--- identity'''
        'hostnamectl --static 2>/dev/null || hostname'
        'echo ''--- lsblk'''
        'lsblk -J -b -o NAME,PATH,TYPE,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINT'
        'echo ''--- root'''
        'findmnt -n -o SOURCE / 2>/dev/null || echo unknown'
        'echo ''--- longhorn'''
        'findmnt -n -o SOURCE,FSTYPE,SIZE /var/lib/longhorn 2>/dev/null || echo none'
        'echo ''--- mount'''
        "findmnt -n -o SOURCE,FSTYPE,SIZE,OPTIONS $MountPoint 2>/dev/null || echo none"
        'echo ''--- stray'''
        "test -d $MountPoint && find $MountPoint -mindepth 1 -maxdepth 1 2>/dev/null | head -n 5 || true"
        'echo ''--- fstab'''
        'grep -v ''^[[:space:]]*#'' /etc/fstab | grep . || true'
        'echo ''--- packages'''
        'dpkg-query -W -f=''${binary:Package} ${Status}\n'' lvm2 2>/dev/null || true'
        'echo ''--- k3s'''
        'command -v k3s || echo missing'
        'echo ''--- lhnode'''
        "sudo k3s kubectl -n longhorn-system get nodes.longhorn.io $hostname -o json 2>/dev/null || echo missing"
        'echo ''--- k8slabels'''
        # --show-labels rather than -o jsonpath: the label key contains both a
        # dot and a slash, and every jsonpath spelling that survives them needs
        # nested quotes that Invoke-NodeSsh's no-double-quote rule forbids.
        # awk takes the last column, which is the comma-separated label list.
        "sudo k3s kubectl get node $hostname --show-labels --no-headers 2>/dev/null | awk '{print `$NF}' || echo missing"
        'echo ''--- end'''
    ) -join '; '

    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 20
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdErr)"
    }

    # The node names itself before anything is written to one of its disks.
    $reportedHostname = (Get-ProbeSection -Output $probe.StdOut -Name 'identity').Trim()
    if ($reportedHostname -and $reportedHostname -ne $hostname) {
        throw "$IPAddress calls itself '$reportedHostname', but -VMName says '$hostname'. This step formats a disk on whatever answers that address, so it stops rather than guessing which of the two is wrong."
    }

    $devices = ConvertTo-DeviceList -Json (Get-ProbeSection -Output $probe.StdOut -Name 'lsblk')
    if ($devices.Count -eq 0) {
        throw "lsblk returned no block devices on $IPAddress, which cannot be true. Raw probe output:`n$($probe.StdOut)"
    }

    $rootSource = (Get-ProbeSection -Output $probe.StdOut -Name 'root').Trim()
    $longhornMount = (Get-ProbeSection -Output $probe.StdOut -Name 'longhorn').Trim()
    $mountLine = (Get-ProbeSection -Output $probe.StdOut -Name 'mount').Trim()
    $strayEntries = @((Get-ProbeSection -Output $probe.StdOut -Name 'stray') -split "`n" | Where-Object { $_.Trim() })
    $fstabLines = @((Get-ProbeSection -Output $probe.StdOut -Name 'fstab') -split "`n" | Where-Object { $_.Trim() })
    $packageLines = @((Get-ProbeSection -Output $probe.StdOut -Name 'packages') -split "`n" | Where-Object { $_.Trim() })
    $k3sPath = (Get-ProbeSection -Output $probe.StdOut -Name 'k3s').Trim()
    $lhNodeJson = (Get-ProbeSection -Output $probe.StdOut -Name 'lhnode').Trim()
    $k8sLabels = @((Get-ProbeSection -Output $probe.StdOut -Name 'k8slabels').Trim() -split ',' | Where-Object { $_.Trim() })

    $installed = @($packageLines | Where-Object { $_ -match '\sinstall ok installed\s*$' } | ForEach-Object { ($_ -split '\s+')[0] })
    $missingPackages = @($RequiredPackages | Where-Object { $installed -notcontains $_ })

    # This script is downstream of Initialize-NodeStorage.ps1 and of Longhorn
    # itself, and both preconditions are cheap to check and expensive to
    # discover later. A node whose /var/lib/longhorn is on the root filesystem
    # is a node that skipped step 5 of the provisioning pipeline, and adding a
    # second disk to it would paper over that.
    if (-not $longhornMount -or $longhornMount -eq 'none') {
        throw "/var/lib/longhorn is not a mount on $IPAddress, so this node never had provision-5-node-storage.yml run against it. The bulk disk is an addition to a prepared node, not a substitute for preparing one - run Initialize-NodeStorage.ps1 first."
    }
    if ($k3sPath -eq 'missing') {
        throw "k3s is not on PATH on $IPAddress. The last stage of this script registers the disk with Longhorn through the Kubernetes API, which it reaches with 'sudo k3s kubectl' on the node itself."
    }
    if (-not $lhNodeJson -or $lhNodeJson -eq 'missing') {
        throw "No node.longhorn.io/$hostname in namespace longhorn-system on $IPAddress. Longhorn has to be installed and this node registered with it before a disk can be added - this step runs after the cluster is up, unlike its sibling. (If 'sudo k3s kubectl' needs a password here, that is the actual failure.)"
    }

    $lhNode = $lhNodeJson | ConvertFrom-Json
    $lhSpec = Get-Field $lhNode 'spec'
    $lhDisks = if ($lhSpec) { Get-Field $lhSpec 'disks' } else { $null }
    $existingDiskEntry = if ($lhDisks) { Get-Field $lhDisks $LonghornDiskName } else { $null }
    $nodeLabelPresent = $k8sLabels -contains "$NodeLabelKey=$NodeLabelValue"

    $lvm = Get-NodeLvmState -Ssh $ssh

    Write-Host "Hostname:  $reportedHostname"
    Write-Host "Root fs:   $rootSource"
    Write-Host "Longhorn:  /var/lib/longhorn <- $longhornMount"
    Write-Host "Packages:  $(if ($missingPackages.Count -eq 0) { 'lvm2 present' } else { "missing $($missingPackages -join ', ')" })"
    Write-Host "$($MountPoint): $(if ($mountLine -and $mountLine -ne 'none') { $mountLine } else { 'not mounted' })"
    Write-Host ''
    $devices |
        Where-Object { $_.Type -eq 'disk' } |
        ForEach-Object {
            $disk = $_
            $children = @($devices | Where-Object { $_.Disk -eq $disk.Path -and $_.Path -ne $disk.Path })
            [pscustomobject]@{
                Disk       = $disk.Path
                Size       = Format-Size $disk.Size
                FsType     = if ($disk.FsType) { $disk.FsType } else { '-' }
                Label      = if ($disk.Label) { $disk.Label } else { '-' }
                Partitions = $children.Count
                MountedAt  = (@(@($disk) + $children | Where-Object { $_.MountPoint } | ForEach-Object { $_.MountPoint }) -join ' ')
            }
        } |
        Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host "LVM:       $(if ($lvm.Vgs.Count) { ($lvm.Vgs | ForEach-Object { "$($_.VgName) ($($_.PvCount) PV, $(Format-Size ([int64][double]$_.Size)))" }) -join ', ' } else { 'no volume groups' })"
        Write-Host "Longhorn disk '$LonghornDiskName': $(if ($existingDiskEntry) { 'already registered' } else { 'not registered' })"
        Write-Host "Node label $($NodeLabelKey): $(if ($nodeLabelPresent) { 'already set' } else { 'not set' })"
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here. Nothing on the node was changed.' -ForegroundColor Yellow
        return
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Packages'
    # ---------------------------------------------------------------- #

    if ($missingPackages.Count -eq 0) {
        Write-Host 'lvm2: already installed.'
    }
    else {
        # `sudo env DEBIAN_FRONTEND=...`, not a bare `sudo VAR=val` prefix:
        # most sudoers policies reset the environment before exec, and a
        # dropped DEBIAN_FRONTEND is an apt that blocks on a configuration
        # prompt no one will ever answer.
        Write-Host "Installing $($missingPackages -join ', ') ..."
        $install = Invoke-NodeSsh @ssh -ConnectTimeoutSec 600 -Command (
            'sudo env DEBIAN_FRONTEND=noninteractive apt-get update -qq && sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq {0}' -f ($missingPackages -join ' ')
        )
        if ($install.ExitCode -ne 0) {
            throw "Installing $($missingPackages -join ', ') on $IPAddress failed (exit $($install.ExitCode)):`n$($install.StdOut)$($install.StdErr)"
        }
        $actions.Add("installed $($missingPackages -join ', ')")
        Write-Host '  installed.'

        # Re-read, because the probe above ran before lvm2 existed and could
        # not have reported a volume group even if the metadata was on a disk
        # all along - which is exactly the situation on a node where a
        # previous run got this far and then failed.
        $lvm = Get-NodeLvmState -Ssh $ssh
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Disk'
    # ---------------------------------------------------------------- #

    # --- what LVM already has ----------------------------------------- #
    $vgEntry = @($lvm.Vgs | Where-Object { $_.VgName -eq $VolumeGroup })
    $lvEntry = @($lvm.Lvs | Where-Object { $_.VgName -eq $VolumeGroup -and $_.LvName -eq $LogicalVolume })
    $vgPvs = @($lvm.Pvs | Where-Object { $_.VgName -eq $VolumeGroup } | ForEach-Object { $_.PvName })
    $allPvs = @($lvm.Pvs | ForEach-Object { $_.PvName })
    $vgExists = $vgEntry.Count -gt 0
    $lvExists = $lvEntry.Count -gt 0

    if ($ExtendVolumeGroup -and -not $vgExists) {
        throw "-ExtendVolumeGroup was passed but $VolumeGroup does not exist on $IPAddress. There is nothing to extend - run without the switch to create it."
    }

    # --- pick the physical disk, when one is needed -------------------- #
    #
    # Never by name: /dev/sdb is not stable across reboots under Hyper-V, and
    # a script that hardcodes it is one disk-controller reorder away from
    # formatting the wrong thing. The two hard rules below are the same two
    # Initialize-NodeStorage.ps1 enforces and -Force does not relax either.
    $needsNewDisk = $ExtendVolumeGroup -or -not $vgExists
    $bulkDisk = $null
    $howFound = $null
    $needsWipe = $false

    if ($needsNewDisk) {
        $diskEntries = @($devices | Where-Object { $_.Type -eq 'disk' })
        $diskState = @{}
        foreach ($disk in $diskEntries) {
            $subtree = @($devices | Where-Object { $_.Disk -eq $disk.Path })
            $mountPoints = @($subtree | Where-Object { $_.MountPoint } | ForEach-Object { $_.MountPoint })
            $holdsRoot = ($mountPoints -contains '/') -or ($rootSource -and @($subtree | Where-Object { $_.Path -eq $rootSource }).Count -gt 0)
            $diskState[$disk.Path] = [pscustomobject]@{
                Disk        = $disk
                MountPoints = $mountPoints
                HoldsRoot   = $holdsRoot
                InUse       = @($mountPoints | Where-Object { $_ -ne $MountPoint }).Count -gt 0 -or $holdsRoot
                # An existing physical volume is never a candidate even under
                # -Force: it is either this script's own disk on a re-run or
                # someone else's data, and both answers mean "not this one".
                IsPv        = $allPvs -contains $disk.Path
                IsEmpty     = $subtree.Count -eq 1 -and -not $disk.FsType -and -not $disk.MountPoint
                SizeOk      = [math]::Abs($disk.Size - $expectedBytes) -le ($expectedBytes * $SizeTolerancePercent / 100)
            }
        }

        $candidates = @($diskEntries | Where-Object {
                $state = $diskState[$_.Path]
                $state.SizeOk -and -not $state.InUse -and -not $state.IsPv -and ($Force -or $state.IsEmpty)
            })

        if ($candidates.Count -eq 0) {
            $why = foreach ($disk in $diskEntries) {
                $state = $diskState[$disk.Path]
                $reasons = New-Object Collections.Generic.List[string]
                if (-not $state.SizeOk) { $reasons.Add("size is $(Format-Size $disk.Size), expected about $(Format-Size $expectedBytes)") }
                if ($state.HoldsRoot) { $reasons.Add('holds the root filesystem') }
                elseif ($state.InUse) { $reasons.Add("something is mounted from it ($($state.MountPoints -join ', '))") }
                elseif ($state.IsPv) { $reasons.Add('is already an LVM physical volume') }
                elseif (-not $state.IsEmpty) { $reasons.Add("already partitioned or formatted$(if ($disk.FsType) { " ($($disk.FsType))" }) - pass -Force to wipe it") }
                "  - $($disk.Path) ($(Format-Size $disk.Size)): $($reasons -join '; ')"
            }
            throw "No bulk disk found on $IPAddress. Looking for an unpartitioned, empty disk of about $(Format-Size $expectedBytes) (-BulkDiskSizeGB $BulkDiskSizeGB, +/-${SizeTolerancePercent}%). What is there:`n$($why -join "`n")`n`nIf the disk was only just attached to the VM, the guest may not have rescanned its SCSI bus yet."
        }
        if ($candidates.Count -gt 1) {
            $listed = ($candidates | ForEach-Object { "$($_.Path) ($(Format-Size $_.Size))" }) -join ', '
            throw "$IPAddress has $($candidates.Count) disks that could equally be the bulk disk: $listed. This step refuses ambiguity rather than picking one - detach the disk that isn't the library's, or narrow -BulkDiskSizeGB so only one matches."
        }

        $bulkDisk = $candidates[0].Path
        $howFound = "the only $(if ($Force) { '' } else { 'empty, unpartitioned ' })disk of about $(Format-Size $expectedBytes)"
        $needsWipe = -not $diskState[$bulkDisk].IsEmpty
    }
    else {
        $bulkDisk = ($vgPvs -join ', ')
        $howFound = "already a physical volume in $VolumeGroup"
    }

    $mountedSource = $null
    if ($mountLine -and $mountLine -ne 'none') {
        $mountedSource = ($mountLine -split '\s+')[0]
    }

    if ($strayEntries.Count -gt 0 -and -not $mountedSource) {
        Write-Warning "$MountPoint already has content on $IPAddress's *root* filesystem while nothing is mounted there ($($strayEntries -join ', ')). Mounting over it hides it without reclaiming the space; check it by hand once this run finishes."
    }

    Write-Host "Disk:      $bulkDisk - $howFound."
    Write-Host "Plan:      $(if ($needsWipe) { "WIPE $bulkDisk; " })$(if ($ExtendVolumeGroup) { "vgextend $VolumeGroup onto $bulkDisk; lvextend; resize2fs; " } elseif (-not $vgExists) { "pvcreate; vgcreate $VolumeGroup; " })$(if (-not $lvExists) { "lvcreate $LogicalVolume; mkfs.ext4; " })reconcile the $MountPoint fstab entry by UUID$(if (-not $mountedSource) { '; mount it' }); register '$LonghornDiskName' with Longhorn."

    if ($needsWipe) {
        Write-Warning "-Force: wiping $bulkDisk on $IPAddress."
        $wipe = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            'sudo wipefs -a {0} && sudo udevadm settle' -f $bulkDisk
        )
        if ($wipe.ExitCode -ne 0) {
            throw "Wiping $bulkDisk on $IPAddress failed (exit $($wipe.ExitCode)):`n$($wipe.StdOut)$($wipe.StdErr)"
        }
        $actions.Add("wiped $bulkDisk")
        Write-Host '  wiped.'
    }

    if ($ExtendVolumeGroup) {
        # Online, in this order, and none of it moves data: vgextend adds
        # capacity to the group, lvextend claims it for the logical volume,
        # and resize2fs grows the ext4 into it while it is mounted. The
        # volume never detaches, so Longhorn never sees the disk go away.
        Write-Host "Extending $VolumeGroup onto $bulkDisk ..."
        $extend = Invoke-NodeSsh @ssh -ConnectTimeoutSec 600 -Command (
            'sudo pvcreate {0} && sudo vgextend {1} {0} && sudo lvextend -l +100%FREE {2} && sudo resize2fs {2}' -f $bulkDisk, $VolumeGroup, $lvPath
        )
        if ($extend.ExitCode -ne 0) {
            throw "Extending $VolumeGroup onto $bulkDisk ($IPAddress) failed (exit $($extend.ExitCode)):`n$($extend.StdOut)$($extend.StdErr)"
        }
        $actions.Add("extended $VolumeGroup onto $bulkDisk and grew $MountPoint")
        Write-Host '  extended.'
    }
    elseif (-not $vgExists) {
        # Whole-disk physical volume, with no partition table. LVM needs a
        # block device, not a partition, and a single-partition table would
        # only add another layer whose device name can be reordered.
        Write-Host "Creating $VolumeGroup on $bulkDisk ..."
        $create = Invoke-NodeSsh @ssh -ConnectTimeoutSec 300 -Command (
            'sudo pvcreate {0} && sudo vgcreate {1} {0} && sudo udevadm settle' -f $bulkDisk, $VolumeGroup
        )
        if ($create.ExitCode -ne 0) {
            throw "Creating $VolumeGroup on $bulkDisk ($IPAddress) failed (exit $($create.ExitCode)):`n$($create.StdOut)$($create.StdErr)"
        }
        $actions.Add("created physical volume $bulkDisk and volume group $VolumeGroup")
        Write-Host '  created.'
    }
    else {
        Write-Host "$VolumeGroup already exists on $($vgPvs -join ', ') - not recreating."
    }

    if ($lvExists) {
        Write-Host "$lvPath already exists - not recreating or reformatting."
    }
    else {
        # -l 100%FREE, so the logical volume is the whole group. There is one
        # consumer of this disk and sizing it smaller would only create free
        # space nothing can reach without an lvextend anyway.
        #
        # -m 0 on mkfs because the 5% ext4 reserves for root is a
        # root-filesystem protection: on a 1200 GiB bulk disk it is 60 GiB set
        # aside for nothing.
        Write-Host "Creating $lvPath and formatting it ext4, label '$FilesystemLabel' ..."
        $mklv = Invoke-NodeSsh @ssh -ConnectTimeoutSec 600 -Command (
            'sudo lvcreate -y -l 100%FREE -n {0} {1} && sudo udevadm settle && sudo mkfs.ext4 -F -m 0 -L {2} {3}' -f $LogicalVolume, $VolumeGroup, $FilesystemLabel, $lvPath
        )
        if ($mklv.ExitCode -ne 0) {
            throw "Creating or formatting $lvPath on $IPAddress failed (exit $($mklv.ExitCode)):`n$($mklv.StdOut)$($mklv.StdErr)"
        }
        $actions.Add("created $lvPath and formatted it ext4")
        Write-Host '  created and formatted.'
    }

    $blkid = Invoke-NodeSsh @ssh -Command ('sudo blkid -s UUID -o value {0}' -f $lvPath) -ConnectTimeoutSec 20
    $uuid = $blkid.StdOut.Trim()
    if ($blkid.ExitCode -ne 0 -or $uuid -notmatch '^[0-9a-fA-F-]{36}$') {
        throw "Couldn't read a filesystem UUID for $lvPath on $IPAddress (exit $($blkid.ExitCode), got '$uuid'). The fstab entry is keyed by UUID, so there is nothing to write without it."
    }
    Write-Host "UUID:      $uuid"

    # `nofail`, and the immutable directory below is what makes that safe.
    # Without nofail a missing data disk stops the boot in emergency mode - on
    # a headless VM that means a node that is simply gone until someone opens
    # vmconnect. With nofail the node boots, and the empty mount point
    # underneath is left immutable so Longhorn cannot quietly write a
    # terabyte of replica data to the OS disk in the mount's absence: it fails
    # loudly with EPERM instead.
    $fstabEntry = 'UUID={0} {1} ext4 defaults,nofail,x-systemd.device-timeout=30s 0 2' -f $uuid, $MountPoint
    $mountPointRegex = $MountPoint -replace '/', '\/'

    # One command, because a half-applied fstab is a node that does not boot:
    # the original is backed up once, the new file is assembled in /tmp, and
    # only a complete assembly is copied into place. Joined with && rather
    # than ; because `>` truncates the scratch file before awk runs, so under
    # ; a failed awk would install an /etc/fstab with no root filesystem in it.
    $fstabScript = @(
        'sudo test -f /etc/fstab.aerie-orig || sudo cp /etc/fstab /etc/fstab.aerie-orig'
        'awk ''$2 !~ /^{0}$/'' /etc/fstab > /tmp/fstab.aerie'
        'printf ''%s\n'' ''{1}'' >> /tmp/fstab.aerie'
        'sudo cp /tmp/fstab.aerie /etc/fstab'
        'rm -f /tmp/fstab.aerie'
        'sudo systemctl daemon-reload'
    ) -join ' && '

    $currentEntries = @($fstabLines | Where-Object { ($_ -split '\s+').Count -ge 2 -and ($_ -split '\s+')[1] -eq $MountPoint })
    $fstabCorrect = $currentEntries.Count -eq 1 -and (($currentEntries[0] -replace '\s+', ' ').Trim() -eq $fstabEntry)

    if ($fstabCorrect) {
        Write-Host "fstab:     already $fstabEntry"
    }
    else {
        $writeFstab = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command ($fstabScript -f $mountPointRegex, $fstabEntry)
        if ($writeFstab.ExitCode -ne 0) {
            throw "Rewriting /etc/fstab on $IPAddress failed (exit $($writeFstab.ExitCode)). The original is at /etc/fstab.aerie-orig:`n$($writeFstab.StdOut)$($writeFstab.StdErr)"
        }
        $actions.Add("keyed $MountPoint to UUID=$uuid in /etc/fstab")
        Write-Host "fstab:     $fstabEntry"
    }

    if ($mountedSource) {
        Write-Host "$MountPoint is already mounted from $mountedSource - left alone."
    }
    else {
        # chattr is best-effort: it hardens a failure mode, it is not what
        # makes the mount work, and a filesystem that does not support the
        # attribute should not fail a provisioning run over it.
        $mountScript = @(
            "sudo mkdir -p $MountPoint"
            "sudo chattr +i $MountPoint || echo 'chattr unsupported'"
            "sudo mount $MountPoint"
        ) -join ' && '
        $mount = Invoke-NodeSsh @ssh -Command $mountScript -ConnectTimeoutSec 60
        if ($mount.ExitCode -ne 0) {
            throw "Mounting $MountPoint on $IPAddress failed (exit $($mount.ExitCode)):`n$($mount.StdOut)$($mount.StdErr)"
        }
        if ($mount.StdOut -match 'chattr unsupported') {
            Write-Warning "Couldn't set the immutable flag on $MountPoint's underlying directory. The mount is fine; what is lost is the guard that stops Longhorn writing to the OS disk if the bulk disk ever fails to mount at boot."
        }
        $actions.Add("mounted $lvPath at $MountPoint")
        Write-Host "  mounted $lvPath at $MountPoint."
    }

    # After the mount, not before it: findmnt --verify counts a mount point
    # that does not exist yet as an error, and on a first run this directory
    # is created two lines above. What it adds over `mount` having succeeded
    # is the boot-time reading - duplicate targets, an unresolvable UUID, a
    # source that will not be there when systemd's generator runs.
    $verifyFstab = Invoke-NodeSsh @ssh -Command 'sudo findmnt --verify' -ConnectTimeoutSec 20
    if ($verifyFstab.ExitCode -ne 0) {
        throw "findmnt --verify rejects /etc/fstab on $IPAddress after this run's edit (exit $($verifyFstab.ExitCode)). $MountPoint is mounted now but would not necessarily come back after a reboot. The original fstab is at /etc/fstab.aerie-orig:`n$($verifyFstab.StdOut)$($verifyFstab.StdErr)"
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Longhorn'
    # ---------------------------------------------------------------- #

    # The tag is the entire safety mechanism. Longhorn schedules a replica
    # onto any disk with capacity unless the volume's StorageClass names a
    # diskSelector, and a class that names none matches every *untagged* disk.
    # Tagging this one takes it out of the general pool, so Prometheus and
    # OpenSearch keep landing on the 200GB disks and only longhorn-bulk's
    # diskSelector: "bulk" can reach this one.
    #
    # storageReserved: 0 because the reservation exists to stop Longhorn
    # filling a disk it shares with an OS. This disk is shared with nothing.
    $desired = [ordered]@{
        path            = $MountPoint
        allowScheduling = $true
        evictionRequested = $false
        storageReserved = 0
        diskType        = 'filesystem'
        tags            = @($DiskTag)
    }

    $alreadyCorrect = $false
    if ($existingDiskEntry) {
        $currentTags = @(Get-Field $existingDiskEntry 'tags' | Where-Object { $_ })
        $alreadyCorrect = ([string](Get-Field $existingDiskEntry 'path') -eq $MountPoint) -and
                          ($currentTags.Count -eq 1 -and $currentTags[0] -eq $DiskTag) -and
                          ([bool](Get-Field $existingDiskEntry 'allowScheduling') -eq $true)
    }

    if ($alreadyCorrect) {
        Write-Host "node.longhorn.io/$hostname already carries '$LonghornDiskName' at $MountPoint, tags [$DiskTag] - not patched."
    }
    else {
        # A JSON merge patch, so the disks map is merged key by key and the
        # chart's own default-disk-<hex> entry survives untouched. A strategic
        # merge patch is not an option: custom resources have no patch
        # strategy, and a replace would drop the default disk and with it
        # every replica on this node.
        #
        # The JSON goes over standard input rather than in the command,
        # because Invoke-NodeSsh refuses a command containing a double quote -
        # Windows PowerShell 5.1 does not escape one when it builds ssh.exe's
        # command line and ssh.exe's parser then strips it, which would land a
        # syntactically valid but semantically different patch on the node.
        $patchJson = @{ spec = @{ disks = @{ $LonghornDiskName = $desired } } } | ConvertTo-Json -Depth 6 -Compress
        $patchScript = @(
            'cat > /tmp/aerie-bulk-disk.json'
            "sudo k3s kubectl -n longhorn-system patch nodes.longhorn.io $hostname --type=merge --patch-file=/tmp/aerie-bulk-disk.json"
            'rm -f /tmp/aerie-bulk-disk.json'
        ) -join ' && '

        Write-Host "Registering '$LonghornDiskName' at $MountPoint with tags [$DiskTag] ..."
        $patch = Invoke-NodeSsh @ssh -Command $patchScript -StdIn $patchJson -ConnectTimeoutSec 60
        if ($patch.ExitCode -ne 0) {
            throw "Patching node.longhorn.io/$hostname on $IPAddress failed (exit $($patch.ExitCode)):`n$($patch.StdOut)$($patch.StdErr)"
        }
        $actions.Add("registered Longhorn disk '$LonghornDiskName' at $MountPoint with tags [$DiskTag]")
        Write-Host '  patched.'
    }

    # The Kubernetes node label, which is the second half of the scheduling
    # contract and the half that took a measurement to discover was needed.
    #
    # The disk tag above governs where Longhorn puts a *replica*. It does not
    # and cannot govern where Kubernetes puts a *pod*, and the longhorn-bulk
    # class's dataLocality: strict-local does not close that gap either -
    # measured on this cluster 2026-08-25, not assumed. Under
    # WaitForFirstConsumer the scheduler chooses a node with no knowledge of
    # Longhorn disk tags (the CSI driver publishes only kubernetes.io/hostname
    # topology and no storage capacity), Longhorn is then told to place the
    # single replica on whatever node was chosen, the diskSelector matches
    # nothing there, and the volume reports 'tags not fulfilled' while the pod
    # stays Pending. The PV is stamped with a hard nodeAffinity to the wrong
    # node on the way past, so it does not self-heal.
    #
    # So the consumer has to be steered by a label, and a label is not the
    # thing docs/ethos.md forbids: the forbidden thing is a *node name* in the
    # repo. This is the same structural fact the disk tag already is - written
    # here, from the same argument, in the same stage, so the two cannot
    # disagree - and deploy/cluster/photos/app/helmrelease.yaml selects on it
    # without knowing which node carries it.
    #
    # --overwrite makes this idempotent even if the key exists with another
    # value. No double quotes, per Invoke-NodeSsh's rule.
    if ($nodeLabelPresent) {
        Write-Host "node/$hostname already carries $NodeLabelKey=$NodeLabelValue - not labelled."
    }
    else {
        Write-Host "Labelling node/$hostname $NodeLabelKey=$NodeLabelValue ..."
        $label = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            "sudo k3s kubectl label node $hostname $NodeLabelKey=$NodeLabelValue --overwrite"
        )
        if ($label.ExitCode -ne 0) {
            throw "Labelling node/$hostname on $IPAddress failed (exit $($label.ExitCode)):`n$($label.StdOut)$($label.StdErr)"
        }
        $actions.Add("labelled node/$hostname $NodeLabelKey=$NodeLabelValue")
        Write-Host '  labelled.'
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    # Re-read from the node rather than trusting what was just done. This is
    # the state Longhorn and the longhorn-bulk StorageClass will actually
    # find, and the plan's exit criterion for this step is these commands.
    $verifyScript = @(
        'echo ''--- findmnt'''
        "findmnt -n -o SOURCE,TARGET,FSTYPE,SIZE,OPTIONS $MountPoint"
        'echo ''--- unit'''
        "systemctl is-active $($MountPoint.Trim('/') -replace '/', '-').mount 2>/dev/null || true"
        'echo ''--- size'''
        "df -B1 --output=size,avail $MountPoint | tail -n 1"
        'echo ''--- fstab'''
        "grep -c $uuid /etc/fstab"
        'echo ''--- k8slabels'''
        "sudo k3s kubectl get node $hostname --show-labels --no-headers | awk '{print `$NF}'"
        'echo ''--- end'''
    ) -join '; '

    $verify = Invoke-NodeSsh @ssh -Command $verifyScript -ConnectTimeoutSec 30
    if ($verify.ExitCode -ne 0) {
        throw "Verification on $IPAddress failed (exit $($verify.ExitCode)):`n$($verify.StdOut)$($verify.StdErr)"
    }

    $findmntLine = (Get-ProbeSection -Output $verify.StdOut -Name 'findmnt').Trim()
    if (-not $findmntLine) {
        throw "$MountPoint is not mounted on $IPAddress after this run. Nothing further should be done with this node until that is understood - a Longhorn disk registered against an unmounted path fills the OS disk."
    }
    $findmntFields = @($findmntLine -split '\s+')
    if ($findmntFields.Count -lt 3) {
        throw "findmnt described $MountPoint on $IPAddress as '$findmntLine', which isn't the SOURCE/TARGET/FSTYPE shape it was asked for."
    }
    if ($findmntFields[2] -ne 'ext4') {
        throw "$MountPoint on $IPAddress is $($findmntFields[2]), not ext4."
    }
    # findmnt names a logical volume by its device-mapper path rather than by
    # the /dev/<vg>/<lv> symlink this script writes, so both spellings are
    # accepted and anything else means something took the mount point.
    $expectedSources = @($lvPath, "/dev/mapper/$VolumeGroup-$LogicalVolume")
    if ($expectedSources -notcontains $findmntFields[0]) {
        throw "$MountPoint on $IPAddress is mounted from $($findmntFields[0]), not from $lvPath. Something else claimed the mount point."
    }

    # Read back rather than trusting the label command's exit code, for the
    # same reason the mount is: this is the state the scheduler will actually
    # find when Immich's server pod asks for a node.
    $verifiedLabels = @((Get-ProbeSection -Output $verify.StdOut -Name 'k8slabels').Trim() -split ',' | Where-Object { $_.Trim() })
    if ($verifiedLabels -notcontains "$NodeLabelKey=$NodeLabelValue") {
        throw "node/$hostname does not carry $NodeLabelKey=$NodeLabelValue after this run. Without it nothing selects this node, and the consumer of a longhorn-bulk volume lands wherever the scheduler likes and then stays Pending on a replica that cannot be placed. Labels seen: $($verifiedLabels -join ', ')"
    }

    $unitState = (Get-ProbeSection -Output $verify.StdOut -Name 'unit').Trim()
    if ($unitState -ne 'active') {
        Write-Warning "systemd reports the generated mount unit for $MountPoint as '$unitState' rather than active. The filesystem is mounted, but confirm it survives a reboot before treating this node as done."
    }

    $sizeFields = @((Get-ProbeSection -Output $verify.StdOut -Name 'size').Trim() -split '\s+' | Where-Object { $_ })
    $capacityBytes = [int64]0
    if ($sizeFields.Count -ge 1) { [void][int64]::TryParse($sizeFields[0], [ref]$capacityBytes) }
    # ext4 metadata - the journal, inode tables, group descriptors - is a few
    # percent of any disk, and LVM itself takes a few megabytes of the group
    # for metadata. The tolerance is that overhead plus -SizeTolerancePercent;
    # what it really checks is that this is the bulk disk and not a 20GB one.
    # In extend mode the filesystem is now larger than one disk, so only the
    # floor is meaningful either way.
    $capacityFloor = $expectedBytes * (100 - $SizeTolerancePercent - 5) / 100
    if (-not $ExtendVolumeGroup -and $capacityBytes -lt $capacityFloor) {
        throw "$MountPoint on $IPAddress has a capacity of $(Format-Size $capacityBytes), well under the $(Format-Size $expectedBytes) bulk disk this run was told to expect. Confirm -BulkDiskSizeGB matches the disk that was attached."
    }

    $fstabMatches = 0
    [void][int]::TryParse((Get-ProbeSection -Output $verify.StdOut -Name 'fstab').Trim(), [ref]$fstabMatches)
    if ($fstabMatches -lt 1) {
        throw "/etc/fstab on $IPAddress no longer mentions UUID=$uuid. The mount is live now but would not survive a reboot, which is the only reason the fstab entry exists."
    }

    # Longhorn's node controller reconciles the CR asynchronously: the patch
    # returns the moment the API server accepts it, and the disk's Ready and
    # Schedulable conditions are written a moment later, after the controller
    # has stat'd the path and written its longhorn-disk.cfg into it. Polling
    # for that is the difference between "the CR says what I asked for" and
    # "Longhorn agrees the disk exists", and only the second one is the gate.
    $deadline = (Get-Date).AddSeconds(120)
    $diskStatus = $null
    $lastReason = 'Longhorn has not reported on the disk yet'
    do {
        $read = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            "sudo k3s kubectl -n longhorn-system get nodes.longhorn.io $hostname -o json"
        )
        # try/catch rather than trusting the exit code: anything on stdout that
        # is not the object - a kubectl deprecation warning, a sudo notice -
        # would otherwise end the run with a JSON parse error instead of being
        # retried, on a loop whose entire job is to tolerate a slow controller.
        $node = $null
        if ($read.ExitCode -eq 0) {
            try { $node = $read.StdOut | ConvertFrom-Json } catch { $lastReason = "the node CR did not parse as JSON: $($_.Exception.Message)" }
        }
        if ($node) {
            $status = Get-Field $node 'status'
            $diskStatuses = if ($status) { Get-Field $status 'diskStatus' } else { $null }
            $diskStatus = if ($diskStatuses) { Get-Field $diskStatuses $LonghornDiskName } else { $null }
            if ($diskStatus) {
                $conditions = @(Get-Field $diskStatus 'conditions' | Where-Object { $_ })
                $ready = @($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Ready' -and (Get-Field $_ 'status') -eq 'True' })
                $schedulable = @($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Schedulable' -and (Get-Field $_ 'status') -eq 'True' })
                if ($ready.Count -gt 0 -and $schedulable.Count -gt 0) { break }
                $notReady = @($conditions | Where-Object { (Get-Field $_ 'status') -ne 'True' } | ForEach-Object { "$(Get-Field $_ 'type')=$(Get-Field $_ 'status') ($(Get-Field $_ 'message'))" })
                if ($notReady.Count -gt 0) { $lastReason = $notReady -join '; ' }
            }
        }
        if ((Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
    } while ((Get-Date) -lt $deadline)

    if (-not $diskStatus) {
        throw "Longhorn never reported a disk called '$LonghornDiskName' on node $hostname within 2 minutes. The filesystem is mounted and the CR was patched, so look at longhorn-manager's logs on this node."
    }
    $conditions = @(Get-Field $diskStatus 'conditions' | Where-Object { $_ })
    $ready = @($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Ready' -and (Get-Field $_ 'status') -eq 'True' })
    $schedulable = @($conditions | Where-Object { (Get-Field $_ 'type') -eq 'Schedulable' -and (Get-Field $_ 'status') -eq 'True' })
    if ($ready.Count -eq 0 -or $schedulable.Count -eq 0) {
        throw "Longhorn sees '$LonghornDiskName' on $hostname but will not schedule to it: $lastReason"
    }

    $storageMaximum = [int64](Get-Field $diskStatus 'storageMaximum')
    $storageAvailable = [int64](Get-Field $diskStatus 'storageAvailable')

    Write-Host $findmntLine
    Write-Host "Capacity:  $(Format-Size $capacityBytes) usable$(if (-not $ExtendVolumeGroup) { " on a $(Format-Size $expectedBytes) disk (the difference is LVM and ext4 metadata)" })."
    Write-Host "Longhorn:  '$LonghornDiskName' Ready and Schedulable, $(Format-Size $storageAvailable) available of $(Format-Size $storageMaximum), tags [$DiskTag]."
    Write-Host "Label:     node/$hostname carries $NodeLabelKey=$NodeLabelValue."

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "'$VMName' bulk storage is ready in ${elapsed} min." -ForegroundColor Green
    if ($actions.Count -eq 0) {
        Write-Host 'Nothing needed changing - this node was already prepared.'
    }
    else {
        Write-Host 'Changed:'
        foreach ($action in $actions) { Write-Host "  - $action" }
    }
    Write-Host ''
    Write-Host 'Next: the longhorn-bulk StorageClass in deploy/cluster/infrastructure/config/'
    Write-Host 'longhorn-storageclasses.yaml selects this disk by its tag, and the node label above'
    Write-Host 'is what puts the consuming pod on the same node. Neither names it, so nothing'
    Write-Host 'downstream needs to know that the node is this one.'

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $lines = @(
            "## Bulk storage prepared on $tick$VMName$tick"
            ''
            '| | |'
            '|---|---|'
            "| Address | $tick$IPAddress$tick |"
            "| Physical disk | $tick$bulkDisk$tick - $howFound |"
            "| Volume | $tick$lvPath$tick in $tick$VolumeGroup$tick |"
            "| Filesystem | ext4, label $tick$FilesystemLabel$tick, $tick$uuid$tick |"
            "| Mount | $tick$MountPoint$tick, $(Format-Size $capacityBytes) usable |"
            "| Longhorn disk | $tick$LonghornDiskName$tick, tags $tick[$DiskTag]$tick, $(Format-Size $storageAvailable) available |"
            "| Node label | $tick$NodeLabelKey=$NodeLabelValue$tick |"
            "| Extended | $($ExtendVolumeGroup.IsPresent) |"
            "| Wiped | $needsWipe |"
            "| Elapsed | ${elapsed} min |"
            ''
            "**Changed:** $(if ($actions.Count -eq 0) { 'nothing - already prepared' } else { ($actions -join '; ') })"
            ''
            "<details><summary>findmnt $MountPoint</summary>"
            ''
            $fence
            $findmntLine
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
