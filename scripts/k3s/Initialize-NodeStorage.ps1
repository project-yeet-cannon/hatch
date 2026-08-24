<#
.SYNOPSIS
    Prepares one k3s node's Longhorn data disk over SSH: installs the host
    packages Longhorn needs, finds the data disk by shape rather than by name,
    formats it, and mounts it at /var/lib/longhorn from /etc/fstab by UUID.

.DESCRIPTION
    the cluster plan Phase 3b's second step, and the last one that runs **once per
    node** - everything after it is a commit under deploy/. Same shape as
    Install-K3sNode.ps1: it reuses ..\hyperv\lib\AerieSsh.ps1 to reach a node
    that Phase 1 built and Phase 2 installed k3s on, so the same key material
    and the same OpenSSH-client prerequisite apply.

    It has to run *before* step 11 installs Longhorn, unavoidably. Longhorn's
    default data path is /var/lib/longhorn on the root filesystem, so a cluster
    that gets Longhorn first quietly fills every node's OS disk with replica
    data while the 200GB disk attached for exactly this purpose sits idle - and
    the first symptom is a node under disk pressure, not a storage error.

    Stages:
      1. Preflight - SSH key resolves, the OpenSSH client is present, and the
                     node answers port 22. Cheap failures before anything
                     touches a disk.
      2. Inspect   - one read-only probe collects the node's identity, its
                     whole block-device tree, the current /var/lib/longhorn
                     mount, /etc/fstab, and the state of the four packages
                     this cares about. Everything below is decided from that
                     snapshot, and the decision is printed before it is acted
                     on. -PreflightOnly stops here.
      3. Packages  - open-iscsi, nfs-common and cryptsetup, then `systemctl
                     enable --now iscsid`. These are *node* packages: Longhorn
                     attaches volumes over iSCSI to the host, not into a
                     container, so the initiator has to exist out here.
                     multipath-tools is expected to be absent on a Debian
                     cloud image; if it isn't, Longhorn's devices are
                     blacklisted from multipathd rather than fought with.
      4. Disk      - identifies the data disk, formats it ext4, and mounts it
                     at /var/lib/longhorn from an fstab entry keyed by UUID.
      5. Verify    - re-reads the mount, the fstab entry, the systemd mount
                     unit and iscsid, and asserts the capacity is the disk
                     that was asked for.

    Idempotent: a re-run against a prepared node finds the disk already
    mounted, reconciles the fstab entry, and verifies. Nothing is formatted
    twice, and -Force is required before anything that already holds data is
    written to.

.PARAMETER DataDiskSizeGB
    The size the data disk was created at - Phase 1's -DataDiskSizeGB, 200 by
    default. This is what makes the disk identifiable without naming a device:
    the right disk is the unpartitioned, empty one of about this size. Sizes
    are binary here, as in New-AerieVM.ps1 (`$DataDiskSizeGB * 1GB`), so 200
    means 200 GiB.

.PARAMETER Force
    DESTRUCTIVE. Allows a candidate disk that already carries a partition
    table or a filesystem to be wiped and reformatted. Without it such a disk
    is refused, because "an unpartitioned disk of the expected size" is the
    only evidence this script has that it found the data disk and not
    something that matters. -Force never widens the two hard rules: a disk
    with anything mounted from it, anywhere in its tree, is never a candidate,
    and neither is the disk holding the root filesystem.

.EXAMPLE
    .\Initialize-NodeStorage.ps1 -VMName aerie-node-1 -IPAddress 10.0.0.21 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Look at what it would do, without touching the node
    .\Initialize-NodeStorage.ps1 -VMName aerie-node-2 -IPAddress 10.0.0.22 `
        -SshPrivateKeyPath ~\.ssh\id_ed25519 -PreflightOnly
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
    [int]$DataDiskSizeGB = 200,

    # A VHDX is exactly the size it was created at, so this could be zero for
    # Aerie's own nodes. It isn't, because passthrough of a whole physical
    # disk is a supported alternative (scripts/hyperv/README.md), and a "2TB"
    # disk is never 2 * 2^40 bytes.
    [ValidateRange(0, 50)]
    [int]$SizeTolerancePercent = 5,

    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,15}$')]
    [string]$FilesystemLabel = 'longhorn',

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [switch]$Force,

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')
# Write-Stage, Get-Field, Get-ProbeSection, ConvertTo-DeviceList and
# Format-Size used to be defined here. They moved when Add-BulkDisk.ps1
# arrived needing every one of them unchanged - see the header of that file.
. (Join-Path $PSScriptRoot 'lib\AerieNodeDisk.ps1')

# Not a parameter, deliberately. Longhorn's defaultDataPath (3b.11) has to be
# this same path, and the two are set in different files by different means -
# a mount point that varies per run is a silent mismatch where Longhorn writes
# to the root disk and reports nothing wrong. It is structural, so it is fixed
# here in the same way the chart versions are fixed in the manifests.
$MountPoint = '/var/lib/longhorn'

# Packages Longhorn needs on the *host*. open-iscsi carries iscsiadm and
# iscsid (v1 volumes attach over iSCSI to the node); nfs-common is what a RWX
# volume's share-manager is mounted with; cryptsetup is required before an
# encrypted volume can be created, and installing it later means finding that
# out from a failed PVC.
$RequiredPackages = @('open-iscsi', 'nfs-common', 'cryptsetup')

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$expectedBytes = [int64]$DataDiskSizeGB * 1GB
$hostname = $VMName.ToLowerInvariant()
$actions = New-Object Collections.Generic.List[string]

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

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
    Write-Host "Data disk: ~${DataDiskSizeGB}GB ($(Format-Size $expectedBytes)), +/-${SizeTolerancePercent}%"
    Write-Host "Mount:     $MountPoint, ext4, label '$FilesystemLabel', by UUID in /etc/fstab"
    if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    if ($Force) { Write-Warning '-Force: a candidate disk that already holds a partition table or a filesystem will be wiped.' }
    Write-Host 'Preflight OK.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-storage-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Inspect'
    # ---------------------------------------------------------------- #

    # Single quotes only, and no double quotes anywhere: Invoke-NodeSsh
    # refuses a command containing one, because Windows PowerShell 5.1 would
    # let ssh.exe strip it and run a subtly different script on the node. The
    # bracket expressions below are inside single quotes for the same reason -
    # unquoted they are globs to the remote shell.
    $probeScript = @(
        'echo ''--- identity'''
        'hostnamectl --static 2>/dev/null || hostname'
        'echo ''--- lsblk'''
        'lsblk -J -b -o NAME,PATH,TYPE,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINT'
        'echo ''--- root'''
        'findmnt -n -o SOURCE / 2>/dev/null || echo unknown'
        'echo ''--- mount'''
        "findmnt -n -o SOURCE,FSTYPE,SIZE,OPTIONS $MountPoint 2>/dev/null || echo none"
        'echo ''--- stray'''
        "test -d $MountPoint && find $MountPoint -mindepth 1 -maxdepth 1 2>/dev/null | head -n 5 || true"
        'echo ''--- fstab'''
        'grep -v ''^[[:space:]]*#'' /etc/fstab | grep . || true'
        'echo ''--- packages'''
        'dpkg-query -W -f=''${binary:Package} ${Status}\n'' open-iscsi nfs-common cryptsetup multipath-tools 2>/dev/null || true'
        'echo ''--- iscsid'''
        'systemctl is-active iscsid 2>/dev/null || true'
        'systemctl is-enabled iscsid 2>/dev/null || true'
        'echo ''--- end'''
    ) -join '; '

    $probe = Invoke-NodeSsh @ssh -Command $probeScript -ConnectTimeoutSec 20
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdErr)"
    }

    # The node names itself before anything is written to one of its disks.
    # Phase 1's cloud-init sets the static hostname from the VM name, so a
    # mismatch here means -IPAddress points at a different machine than
    # -VMName says - which, for a step whose next action is mkfs, is worth one
    # round trip to rule out.
    $reportedHostname = (Get-ProbeSection -Output $probe.StdOut -Name 'identity').Trim()
    if ($reportedHostname -and $reportedHostname -ne $hostname) {
        throw "$IPAddress calls itself '$reportedHostname', but -VMName says '$hostname'. This step formats a disk on whatever answers that address, so it stops rather than guessing which of the two is wrong. Check the DHCP reservation and the address you passed."
    }

    $devices = ConvertTo-DeviceList -Json (Get-ProbeSection -Output $probe.StdOut -Name 'lsblk')
    if ($devices.Count -eq 0) {
        throw "lsblk returned no block devices on $IPAddress, which cannot be true. Raw probe output:`n$($probe.StdOut)"
    }

    $rootSource = (Get-ProbeSection -Output $probe.StdOut -Name 'root').Trim()
    $mountLine = (Get-ProbeSection -Output $probe.StdOut -Name 'mount').Trim()
    $strayEntries = @((Get-ProbeSection -Output $probe.StdOut -Name 'stray') -split "`n" | Where-Object { $_.Trim() })
    $fstabLines = @((Get-ProbeSection -Output $probe.StdOut -Name 'fstab') -split "`n" | Where-Object { $_.Trim() })
    $packageLines = @((Get-ProbeSection -Output $probe.StdOut -Name 'packages') -split "`n" | Where-Object { $_.Trim() })
    $iscsidLines = @((Get-ProbeSection -Output $probe.StdOut -Name 'iscsid') -split "`n" | Where-Object { $_.Trim() })

    $installed = @($packageLines | Where-Object { $_ -match '\sinstall ok installed\s*$' } | ForEach-Object { ($_ -split '\s+')[0] })
    $missingPackages = @($RequiredPackages | Where-Object { $installed -notcontains $_ })
    $multipathPresent = $installed -contains 'multipath-tools'
    $iscsidActive = $iscsidLines.Count -ge 1 -and $iscsidLines[0].Trim() -eq 'active'
    $iscsidEnabled = $iscsidLines.Count -ge 2 -and $iscsidLines[1].Trim() -eq 'enabled'

    Write-Host "Hostname:  $reportedHostname"
    Write-Host "Root fs:   $rootSource"
    Write-Host "Packages:  $(if ($missingPackages.Count -eq 0) { 'all present' } else { "missing $($missingPackages -join ', ')" })$(if ($multipathPresent) { '; multipath-tools IS installed' })"
    Write-Host "iscsid:    $(if ($iscsidLines) { $iscsidLines -join '/' } else { 'not reported' })"
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

    # --- pick the disk ------------------------------------------------ #
    #
    # Three ways to arrive at a device, in descending order of certainty, and
    # never by name: /dev/sdb is not stable across reboots under Hyper-V, and
    # a script that hardcodes it is one disk-controller reorder away from
    # formatting the wrong thing.

    $mountedSource = $null
    if ($mountLine -and $mountLine -ne 'none') {
        $mountedSource = ($mountLine -split '\s+')[0]
    }

    # A disk is untouchable if anything in its tree is mounted, or if it is
    # what / is served from. -Force does not relax either: those are the two
    # ways this script could destroy something that matters, and no flag on a
    # provisioning workflow is worth that.
    $diskEntries = @($devices | Where-Object { $_.Type -eq 'disk' })
    $diskState = @{}
    foreach ($disk in $diskEntries) {
        $subtree = @($devices | Where-Object { $_.Disk -eq $disk.Path })
        $mountPoints = @($subtree | Where-Object { $_.MountPoint } | ForEach-Object { $_.MountPoint })
        $holdsRoot = ($mountPoints -contains '/') -or ($rootSource -and @($subtree | Where-Object { $_.Path -eq $rootSource }).Count -gt 0)
        $diskState[$disk.Path] = [pscustomobject]@{
            Disk        = $disk
            Subtree     = $subtree
            MountPoints = $mountPoints
            HoldsRoot   = $holdsRoot
            # $MountPoint's own mount doesn't count as "in use by something
            # else" - on a re-run it is this script's own previous result.
            InUse       = @($mountPoints | Where-Object { $_ -ne $MountPoint }).Count -gt 0 -or $holdsRoot
            IsEmpty     = $subtree.Count -eq 1 -and -not $disk.FsType -and -not $disk.MountPoint
            SizeOk      = [math]::Abs($disk.Size - $expectedBytes) -le ($expectedBytes * $SizeTolerancePercent / 100)
        }
    }

    $dataDisk = $null
    $howFound = $null

    if ($mountedSource) {
        $dataDisk = $mountedSource
        $howFound = "already mounted at $MountPoint"
    }
    else {
        # Second: a device this script has already labelled. It survives a
        # wiped fstab, a reordered controller and a rebuilt OS disk, which is
        # exactly the situation where guessing by size would be riskiest.
        $labelled = @($devices | Where-Object { $_.Label -eq $FilesystemLabel })
        if ($labelled.Count -gt 1) {
            throw "More than one device on $IPAddress carries the label '$FilesystemLabel' ($(($labelled | ForEach-Object { $_.Path }) -join ', ')). Refusing to guess which one is the data disk - clear the label from whichever is wrong (e2label, with an empty new label) and re-run."
        }
        if ($labelled.Count -eq 1) {
            $dataDisk = $labelled[0].Path
            $howFound = "carries the '$FilesystemLabel' label already"
        }
    }

    if (-not $dataDisk) {
        # Last: shape. The right disk is the one of about the expected size
        # that holds nothing - which is what Phase 1 attached and deliberately
        # left unformatted.
        $candidates = @($diskEntries | Where-Object {
                $state = $diskState[$_.Path]
                $state.SizeOk -and -not $state.InUse -and ($Force -or $state.IsEmpty)
            })

        if ($candidates.Count -eq 0) {
            $why = foreach ($disk in $diskEntries) {
                $state = $diskState[$disk.Path]
                $reasons = New-Object Collections.Generic.List[string]
                if (-not $state.SizeOk) { $reasons.Add("size is $(Format-Size $disk.Size), expected about $(Format-Size $expectedBytes)") }
                if ($state.HoldsRoot) { $reasons.Add('holds the root filesystem') }
                elseif ($state.InUse) { $reasons.Add("something is mounted from it ($($state.MountPoints -join ', '))") }
                elseif (-not $state.IsEmpty) { $reasons.Add("already partitioned or formatted$(if ($disk.FsType) { " ($($disk.FsType))" }) - pass -Force to wipe it") }
                "  - $($disk.Path) ($(Format-Size $disk.Size)): $($reasons -join '; ')"
            }
            throw "No data disk found on $IPAddress. Looking for an unpartitioned, empty disk of about $(Format-Size $expectedBytes) (-DataDiskSizeGB $DataDiskSizeGB, +/-${SizeTolerancePercent}%). What is there:`n$($why -join "`n")"
        }
        if ($candidates.Count -gt 1) {
            $listed = ($candidates | ForEach-Object { "$($_.Path) ($(Format-Size $_.Size))" }) -join ', '
            throw "$IPAddress has $($candidates.Count) disks that could equally be the data disk: $listed. This step refuses ambiguity rather than picking one - detach the disk that isn't Longhorn's, or narrow -DataDiskSizeGB so only one matches."
        }

        $dataDisk = $candidates[0].Path
        $howFound = "the only $(if ($Force) { '' } else { 'empty, unpartitioned ' })disk of about $(Format-Size $expectedBytes)"
    }

    $dataDiskEntry = @($devices | Where-Object { $_.Path -eq $dataDisk })[0]
    if (-not $dataDiskEntry) {
        # Only reachable through the already-mounted branch, and only if the
        # mount's source isn't a plain block device - a device-mapper target,
        # say. Nothing below knows how to reason about that safely.
        throw "$MountPoint on $IPAddress is mounted from '$dataDisk', which isn't one of the block devices lsblk reports. That is not something this step set up, so it stops rather than acting on a device it cannot describe."
    }
    $needsFormat = -not ($dataDiskEntry.FsType -eq 'ext4' -and $dataDiskEntry.Label -eq $FilesystemLabel)
    $needsWipe = $needsFormat -and (
        $dataDiskEntry.FsType -or @($devices | Where-Object { $_.Disk -eq $dataDisk -and $_.Path -ne $dataDisk }).Count -gt 0
    )

    if ($needsFormat -and $mountedSource) {
        throw "$MountPoint on $IPAddress is already mounted from $dataDisk, but that device is $(if ($dataDiskEntry.FsType) { "$($dataDiskEntry.FsType) labelled '$($dataDiskEntry.Label)'" } else { 'not a filesystem this script recognises' }) rather than ext4 labelled '$FilesystemLabel'. Reformatting a mounted data path is not something this will do unattended: unmount it and re-run, after being sure of what is on it."
    }
    if ($needsWipe -and -not $Force) {
        throw "$dataDisk on $IPAddress already holds data and -Force was not passed. This should not be reachable - report it."
    }

    Write-Host "Data disk: $dataDisk - $howFound."
    Write-Host "Plan:      $(if ($missingPackages.Count) { "install $($missingPackages -join ', '); " })$(if (-not $iscsidActive -or -not $iscsidEnabled) { 'enable iscsid; ' })$(if ($multipathPresent) { 'blacklist Longhorn devices in multipathd; ' })$(if ($needsWipe) { "WIPE $dataDisk; " })$(if ($needsFormat) { "mkfs.ext4 $dataDisk; " })reconcile the $MountPoint fstab entry by UUID$(if (-not $mountedSource) { '; mount it' })."

    if ($strayEntries.Count -gt 0 -and -not $mountedSource) {
        Write-Warning "$MountPoint already has content on $IPAddress's *root* filesystem while nothing is mounted there ($($strayEntries -join ', ')). That is the failure this step exists to prevent - something has been writing Longhorn data to the OS disk. Mounting the data disk over it hides it without reclaiming the space; check it by hand once this run finishes."
    }
    if ($fstabLines | Where-Object { ($_ -split '\s+').Count -ge 2 -and ($_ -split '\s+')[1] -eq $MountPoint }) {
        Write-Host "fstab:     an entry for $MountPoint exists already and will be reconciled against this run's UUID."
    }

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here. Nothing on the node was changed.' -ForegroundColor Yellow
        return
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Packages'
    # ---------------------------------------------------------------- #

    if ($missingPackages.Count -eq 0) {
        Write-Host "$($RequiredPackages -join ', '): already installed."
    }
    else {
        # `sudo env DEBIAN_FRONTEND=...`, not a bare `sudo VAR=val` prefix, for
        # the reason Install-K3sNode.ps1 gives: most sudoers policies reset the
        # environment before exec, and a dropped DEBIAN_FRONTEND is an apt that
        # blocks on a configuration prompt no one will ever answer.
        Write-Host "Installing $($missingPackages -join ', ') ..."
        $install = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            'sudo apt-get update -qq && sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -o Dpkg::Use-Pty=0 {0}' -f ($missingPackages -join ' ')
        )
        if ($install.ExitCode -ne 0) {
            throw "Installing $($missingPackages -join ', ') on $IPAddress failed (exit $($install.ExitCode)):`n$($install.StdOut)$($install.StdErr)"
        }
        $actions.Add("installed $($missingPackages -join ', ')")
        Write-Host '  installed.'
    }

    # Unconditional, not conditional on the inspect reading: open-iscsi's
    # postinst does not enable iscsid on every Debian release, and `enable
    # --now` on an already-running unit is a no-op. Cheaper than being wrong.
    $enable = Invoke-NodeSsh @ssh -Command 'sudo systemctl enable --now iscsid' -ConnectTimeoutSec 30
    if ($enable.ExitCode -ne 0) {
        throw "Enabling iscsid on $IPAddress failed (exit $($enable.ExitCode)). Without it, every Longhorn volume attach fails at the node with a message about the iSCSI initiator:`n$($enable.StdOut)$($enable.StdErr)"
    }
    if (-not ($iscsidActive -and $iscsidEnabled)) { $actions.Add('enabled iscsid') }
    Write-Host 'iscsid: enabled and running.'

    if (-not $multipathPresent) {
        Write-Host 'multipath-tools: absent, as expected on a Debian cloud image - nothing to blacklist.'
    }
    else {
        # multipathd claims Longhorn's iSCSI block devices as multipath maps
        # and Longhorn then cannot attach them: the volume sits in Attaching
        # forever, with the actual cause visible only in multipathd's log.
        # Removing the package would be the cleaner fix, but it is on this
        # node because something else wanted it, so the device class is
        # excluded instead.
        Write-Warning 'multipath-tools is installed on this node. Longhorn devices will be blacklisted from multipathd - see /etc/multipath/conf.d/longhorn.conf.'
        $multipathConf = @(
            '# Managed by Aerie: scripts/k3s/Initialize-NodeStorage.ps1 (the cluster plan Phase 3b.2).'
            '# multipathd otherwise claims the block devices Longhorn attaches over iSCSI,'
            '# and the volume stays in Attaching with nothing in Longhorn saying why.'
            'blacklist {'
            '    devnode "^sd[a-z0-9]+"'
            '}'
            ''
        ) -join "`n"

        # Base64 in -Command rather than the content on -StdIn: the content
        # needs double quotes, which Invoke-NodeSsh refuses in -Command, and
        # its -StdIn channel can weld a UTF-8 BOM onto what it carries. A BOM
        # in a multipath.conf is a parse error at the first line. Base64 is
        # immune to both - see Invoke-NodeSsh's own notes.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($multipathConf))
        $writeConf = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command (
            'sudo mkdir -p /etc/multipath/conf.d && echo {0} | base64 -d | sudo tee /etc/multipath/conf.d/longhorn.conf >/dev/null && sudo systemctl restart multipathd' -f $encoded
        )
        if ($writeConf.ExitCode -ne 0) {
            throw "Writing the multipathd blacklist on $IPAddress failed (exit $($writeConf.ExitCode)):`n$($writeConf.StdOut)$($writeConf.StdErr)"
        }
        $actions.Add('blacklisted Longhorn devices in multipathd')
        Write-Host '  /etc/multipath/conf.d/longhorn.conf written, multipathd restarted.'
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Disk'
    # ---------------------------------------------------------------- #

    if ($needsWipe) {
        Write-Warning "-Force: wiping $dataDisk on $IPAddress."
        $wipe = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            'sudo wipefs -a {0} && sudo udevadm settle' -f $dataDisk
        )
        if ($wipe.ExitCode -ne 0) {
            throw "Wiping $dataDisk on $IPAddress failed (exit $($wipe.ExitCode)):`n$($wipe.StdOut)$($wipe.StdErr)"
        }
        $actions.Add("wiped $dataDisk")
        Write-Host '  wiped.'
    }

    if (-not $needsFormat) {
        Write-Host "$dataDisk is already ext4 labelled '$FilesystemLabel' - not reformatting."
    }
    else {
        # Whole-disk ext4, with no partition table. Longhorn's v1 engine wants
        # a filesystem *path*, not a block device, so a partition buys nothing
        # here and costs the one thing that matters: a single-partition table
        # is another layer whose device name can be reordered, and the fstab
        # entry is keyed by the filesystem's UUID either way.
        #
        # -m 0 because the 5% ext4 reserves for root is a root-filesystem
        # protection: on a 200GB data disk it is 10GB set aside for nothing.
        Write-Host "Formatting $dataDisk as ext4, label '$FilesystemLabel' ..."
        $mkfs = Invoke-NodeSsh @ssh -ConnectTimeoutSec 300 -Command (
            'sudo mkfs.ext4 -F -m 0 -L {0} {1}' -f $FilesystemLabel, $dataDisk
        )
        if ($mkfs.ExitCode -ne 0) {
            throw "mkfs.ext4 on $dataDisk ($IPAddress) failed (exit $($mkfs.ExitCode)):`n$($mkfs.StdOut)$($mkfs.StdErr)"
        }
        $actions.Add("formatted $dataDisk as ext4")
        Write-Host '  formatted.'
    }

    $blkid = Invoke-NodeSsh @ssh -Command ('sudo blkid -s UUID -o value {0}' -f $dataDisk) -ConnectTimeoutSec 20
    $uuid = $blkid.StdOut.Trim()
    if ($blkid.ExitCode -ne 0 -or $uuid -notmatch '^[0-9a-fA-F-]{36}$') {
        throw "Couldn't read a filesystem UUID for $dataDisk on $IPAddress (exit $($blkid.ExitCode), got '$uuid'). The fstab entry is keyed by UUID, so there is nothing to write without it."
    }
    Write-Host "UUID:      $uuid"

    # `nofail`, deliberately, and the immutable directory below is what makes
    # that safe. Without nofail a missing or unreadable data disk stops the
    # boot in emergency mode - on a headless VM that means a node that is
    # simply gone until someone opens vmconnect. With nofail the node boots,
    # and the empty mount point underneath is left immutable so nothing can
    # quietly write Longhorn data to the OS disk in the mount's absence:
    # Longhorn fails loudly with EPERM instead, which is the outcome worth
    # engineering for. Mounting over an immutable directory is unaffected -
    # the flag governs writes through the inode, not the mount namespace.
    $fstabEntry = 'UUID={0} {1} ext4 defaults,nofail,x-systemd.device-timeout=30s 0 2' -f $uuid, $MountPoint
    $mountPointRegex = $MountPoint -replace '/', '\/'

    # One command, because a half-applied fstab is a node that does not boot:
    # the original is backed up once, the new file is assembled in /tmp, and
    # only a complete assembly is copied into place.
    #
    # Joined with && rather than ; and that is load-bearing. `>` truncates the
    # scratch file before awk runs, so under ; a failed awk would leave an
    # empty file, the printf would make it a one-line file, and the cp would
    # install an /etc/fstab with no root filesystem in it. `a || b && c` is
    # left-associative in POSIX sh, so the backup line reads as intended:
    # (already backed up OR back it up) AND carry on.
    $fstabScript = @(
        'sudo test -f /etc/fstab.aerie-orig || sudo cp /etc/fstab /etc/fstab.aerie-orig'
        'awk ''$2 !~ /^{0}$/'' /etc/fstab > /tmp/fstab.aerie'
        'printf ''%s\n'' ''{1}'' >> /tmp/fstab.aerie'
        'sudo cp /tmp/fstab.aerie /etc/fstab'
        'rm -f /tmp/fstab.aerie'
        'sudo systemctl daemon-reload'
    ) -join ' && '

    # Compared before it is written, so a re-run against a prepared node
    # reports "nothing changed" and means it. A run that rewrites the file
    # every time and then lists it as a change teaches the reader to skim the
    # change list, which is the one part of this output worth reading.
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
        Write-Host '  (the immutable guard on the directory underneath cannot be read while something is mounted over it)'
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
        $mount = Invoke-NodeSsh @ssh -Command $mountScript -ConnectTimeoutSec 30
        if ($mount.ExitCode -ne 0) {
            throw "Mounting $MountPoint on $IPAddress failed (exit $($mount.ExitCode)):`n$($mount.StdOut)$($mount.StdErr)"
        }
        if ($mount.StdOut -match 'chattr unsupported') {
            Write-Warning "Couldn't set the immutable flag on $MountPoint's underlying directory. The mount is fine; what is lost is the guard that stops Longhorn writing to the OS disk if the data disk ever fails to mount at boot."
        }
        $actions.Add("mounted $dataDisk at $MountPoint")
        Write-Host "  mounted $dataDisk at $MountPoint."
    }

    # After the mount, not before it, and that ordering is not cosmetic:
    # findmnt --verify counts a mount point that does not exist yet as an
    # error, and on a first run this directory is created two lines above.
    # What it adds over `mount` having succeeded is the boot-time reading -
    # duplicate targets, an unresolvable UUID, a source that will not be there
    # when systemd's generator runs. The mount working now does not prove any
    # of that, and the next reboot is the worst time to find out.
    $verifyFstab = Invoke-NodeSsh @ssh -Command 'sudo findmnt --verify' -ConnectTimeoutSec 20
    if ($verifyFstab.ExitCode -ne 0) {
        throw "findmnt --verify rejects /etc/fstab on $IPAddress after this run's edit (exit $($verifyFstab.ExitCode)). $MountPoint is mounted now but would not necessarily come back after a reboot. The original fstab is at /etc/fstab.aerie-orig:`n$($verifyFstab.StdOut)$($verifyFstab.StdErr)"
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    # Re-read from the node rather than trusting what was just done - this is
    # the state Longhorn will actually find at 3b.11, and the cluster plan's exit
    # criterion for this step is literally this command.
    $verifyScript = @(
        'echo ''--- findmnt'''
        "findmnt -n -o SOURCE,TARGET,FSTYPE,SIZE,OPTIONS $MountPoint"
        'echo ''--- unit'''
        "systemctl is-active $($MountPoint.Trim('/') -replace '/', '-').mount 2>/dev/null || true"
        'echo ''--- size'''
        "df -B1 --output=size,avail $MountPoint | tail -n 1"
        'echo ''--- iscsid'''
        'systemctl is-active iscsid'
        'echo ''--- iscsiadm'''
        'command -v iscsiadm || echo missing'
        'echo ''--- fstab'''
        "grep -c $uuid /etc/fstab"
        'echo ''--- end'''
    ) -join '; '

    $verify = Invoke-NodeSsh @ssh -Command $verifyScript -ConnectTimeoutSec 30
    if ($verify.ExitCode -ne 0) {
        throw "Verification on $IPAddress failed (exit $($verify.ExitCode)):`n$($verify.StdOut)$($verify.StdErr)"
    }

    $findmntLine = (Get-ProbeSection -Output $verify.StdOut -Name 'findmnt').Trim()
    if (-not $findmntLine) {
        throw "$MountPoint is not mounted on $IPAddress after this run. Nothing further should be done with this node until that is understood - Longhorn installed against an unmounted data path fills the OS disk."
    }
    # SOURCE TARGET FSTYPE SIZE OPTIONS, in that order, from the -o above.
    $findmntFields = @($findmntLine -split '\s+')
    if ($findmntFields.Count -lt 3) {
        throw "findmnt described $MountPoint on $IPAddress as '$findmntLine', which isn't the SOURCE/TARGET/FSTYPE shape it was asked for. Nothing below can be trusted to have read it correctly."
    }
    if ($findmntFields[0] -ne $dataDisk) {
        throw "$MountPoint on $IPAddress is mounted from $($findmntFields[0]), not from $dataDisk. Something else claimed the mount point."
    }
    if ($findmntFields[2] -ne 'ext4') {
        throw "$MountPoint on $IPAddress is $($findmntFields[2]), not ext4."
    }

    $unitState = (Get-ProbeSection -Output $verify.StdOut -Name 'unit').Trim()
    if ($unitState -ne 'active') {
        # A mount systemd doesn't own is a mount that does not come back after
        # a reboot, which is the whole reason the fstab entry exists.
        Write-Warning "systemd reports the generated mount unit for $MountPoint as '$unitState' rather than active. The filesystem is mounted, but confirm it survives a reboot before treating this node as done."
    }

    # Parsed defensively rather than cast: a df that printed something
    # unexpected should fail the capacity check with the message written for
    # it below, not with a PowerShell conversion error naming a type.
    $sizeFields = @((Get-ProbeSection -Output $verify.StdOut -Name 'size').Trim() -split '\s+' | Where-Object { $_ })
    $capacityBytes = [int64]0
    if ($sizeFields.Count -ge 1) { [void][int64]::TryParse($sizeFields[0], [ref]$capacityBytes) }
    # ext4 metadata - the journal, inode tables, group descriptors - is a few
    # percent of any disk, so the *filesystem* is always smaller than the
    # device. The tolerance below is that overhead plus -SizeTolerancePercent;
    # what it is really checking is that this is the 200GB disk and not a
    # 20GB one, or the root filesystem.
    $capacityFloor = $expectedBytes * (100 - $SizeTolerancePercent - 5) / 100
    if ($capacityBytes -lt $capacityFloor) {
        throw "$MountPoint on $IPAddress has a capacity of $(Format-Size $capacityBytes), well under the $(Format-Size $expectedBytes) data disk this run was told to expect. Confirm -DataDiskSizeGB matches what Phase 1 attached, and that the mount is the data disk rather than the root filesystem."
    }

    if ((Get-ProbeSection -Output $verify.StdOut -Name 'iscsid').Trim() -ne 'active') {
        throw "iscsid is not active on $IPAddress after this run. Longhorn attaches volumes over iSCSI to the node itself, so every attach would fail."
    }
    if ((Get-ProbeSection -Output $verify.StdOut -Name 'iscsiadm').Trim() -eq 'missing') {
        throw "iscsiadm is not on PATH on $IPAddress even though open-iscsi reports installed. Longhorn's node check looks for exactly this binary."
    }
    $fstabMatches = 0
    [void][int]::TryParse((Get-ProbeSection -Output $verify.StdOut -Name 'fstab').Trim(), [ref]$fstabMatches)
    if ($fstabMatches -lt 1) {
        throw "/etc/fstab on $IPAddress no longer mentions UUID=$uuid. The mount is live now but would not survive a reboot, which is the only reason the fstab entry exists."
    }

    Write-Host $findmntLine
    Write-Host "Capacity:  $(Format-Size $capacityBytes) usable on a $(Format-Size $expectedBytes) disk (the difference is ext4 metadata)."
    Write-Host 'iscsid active, iscsiadm present, fstab keyed by UUID, findmnt --verify clean.'

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "'$VMName' storage is ready in ${elapsed} min." -ForegroundColor Green
    if ($actions.Count -eq 0) {
        Write-Host 'Nothing needed changing - this node was already prepared.'
    }
    else {
        Write-Host 'Changed:'
        foreach ($action in $actions) { Write-Host "  - $action" }
    }
    Write-Host ''
    Write-Host 'Run this against every node before Phase 3b.11 installs Longhorn. Longhorn defaults its'
    Write-Host "data path to $MountPoint on the root filesystem, so a node that skipped this step fills"
    Write-Host 'its OS disk with replica data and says nothing about it.'

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $lines = @(
            "## Longhorn storage prepared on $tick$VMName$tick"
            ''
            '| | |'
            '|---|---|'
            "| Address | $tick$IPAddress$tick |"
            "| Data disk | $tick$dataDisk$tick - $howFound |"
            "| Filesystem | ext4, label $tick$FilesystemLabel$tick, $tick$uuid$tick |"
            "| Mount | $tick$MountPoint$tick, $(Format-Size $capacityBytes) usable |"
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
