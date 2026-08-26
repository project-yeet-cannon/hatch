<#
.SYNOPSIS
    Reads back the shape of a node VM on the host it lives on, and asserts it
    against the on-disk layout rule in scripts/hyperv/README.md: fixed OS
    disk, no automatic checkpoints, static memory, the start action that was
    asked for - and, over SSH, the root filesystem the guest actually got.

.DESCRIPTION
    docs/plans/part-time-node.md steps 3.6 and 3.8, as a command rather than
    as two blocks of PowerShell in a plan that someone pastes and reads with
    their eyes. Both steps exist because of the same discovery: **every node
    that exists today was retrofitted rather than built correctly**, by
    Move-NodeOsDisk.ps1, so "a node built by Provision 0 comes out right" is
    a claim nobody has observed. This is what observes it.

    Written to be worth running against any node, not just the part-time one.
    Three of its checks caught real defects on the three permanent nodes -
    an automatic checkpoint quietly layering a dynamic .avhdx over a fixed OS
    disk, a template built at 32 GB, and a root filesystem that never grew -
    and every one of them looked identical to a healthy node in every
    dashboard. That is the property that makes them worth a gate: the failure
    mode is silence.

    Same three rules as every gate under scripts/:

    **It does not stop at the first failure.** A VM with a dynamic disk *and*
    a checkpoint is a different diagnosis from either alone, and it can only
    say so by checking both.

    **A check it cannot evaluate is a failure, not a skip.** A guest that
    will not answer SSH is "not proven", which is a failure - with one
    deliberate exception, -SkipGuestChecks, which drops the guest checks
    entirely rather than pretending to have made them.

    **Expectations come from the arguments and the host, not from a table
    written here.** -OsDiskSizeGB and -MemoryGB are the same numbers passed
    to Provision 0, and the OS disk's path is discovered from the VM rather
    than reconstructed from a convention - a check that rebuilt the path from
    -VMStoragePath would pass against the wrong file on any host where the
    two disks live on different volumes, which is the layout the rule exists
    to produce.

    Read-only. Nothing here starts, stops, or reconfigures anything.

    Stages:
      1. Host    - the VM exists, and its disks and settings are read once.
      2. Shape   - the checks against the layout rule.
      3. Guest   - over SSH: the root filesystem grew into the disk.
      4. Report  - one table, one exit code.

.PARAMETER VMName
    The VM to read. Must exist on this host.

.PARAMETER OsDiskSizeGB
    What the OS disk was asked for. The layout rule's default is 100; 32 was
    the old template size and is what filled every node's root filesystem, so
    a node reporting 32 here is reporting the bug rather than a preference.

.PARAMETER DataDiskSizeGB
    What the Longhorn data disk was asked for. 0 asserts the *absence* of a
    second disk, which is the part-time node's case (that plan's finding 3):
    it joins Longhorn with allowScheduling false and holds no replicas, so a
    data disk on it would be a fixed VHDX doing nothing on a host short of
    space. A data disk found when 0 was asked for is a failure, not a bonus.

.PARAMETER AutomaticStartAction
    What the VM should do when the host boots. `Nothing` for the part-time
    host (finding 6): whether that node should be running is a question whose
    answer is in the personal-mode state file, and Hyper-V cannot read it.

.PARAMETER IPAddress
    The guest's LAN address, for the stage-3 checks. Required unless
    -SkipGuestChecks.

.PARAMETER TemplatePath
    Where the golden VHDX lives. Its provenance file is read for the one
    deliberate exception to the fixed-disk rule - the template is dynamic,
    because it is copied and never booted - and for the size it was built at,
    which is what a per-VM resize either did or did not have to correct.

.EXAMPLE
    # The part-time node, after Provision 0.
    .\Test-NodeVm.ps1 -VMName aerie-node-3 -IPAddress 192.168.1.243 `
        -DataDiskSizeGB 0 -AutomaticStartAction Nothing `
        -SshPrivateKeyPath ~\.ssh\aerie_node

.EXAMPLE
    # A permanent node, checked against the same rule.
    .\Test-NodeVm.ps1 -VMName aerie-node-1 -IPAddress 192.168.1.241 `
        -SshPrivateKeyPath ~\.ssh\aerie_node
#>
#Requires -Modules Hyper-V
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$VMName,

    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [int]$OsDiskSizeGB = 100,
    [int]$DataDiskSizeGB = 200,
    [int]$MemoryGB = 16,

    [ValidateSet('Start', 'Nothing', 'StartIfRunning')]
    [string]$AutomaticStartAction = 'Start',

    [string]$TemplatePath = 'D:\aerie\vm-templates',

    [string]$Username = 'aerie',
    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    # Drops stage 3 rather than failing it. For a VM that is deliberately off
    # - which the part-time node is for most of an evening - where the host
    # checks are still the whole of what can be asked.
    [switch]$SkipGuestChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\AerieSsh.ps1')

# A fixed VHDX is written out in full at creation, so its file is its virtual
# size plus a small footer. Not an equality check: Hyper-V's own metadata adds
# a few MB, and a dynamic disk that happens to be nearly full would still sit
# far below this. 95% separates the two cases with room to spare - a dynamic
# disk that reached 95% of its virtual size has a different problem.
$FixedDiskFillRatio = 0.95

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

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Host'
    # ---------------------------------------------------------------- #

    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if (-not $vm) {
        throw "No VM named '$VMName' on $env:COMPUTERNAME. This script reads a VM on the host it is run from; run it on the host that carries the node, or check the name."
    }
    Write-Host "VM '$VMName' on $env:COMPUTERNAME is $($vm.State), uptime $($vm.Uptime)."

    if (-not $SkipGuestChecks) {
        if (-not $IPAddress) {
            throw 'Stage 3 needs -IPAddress. Pass it, or pass -SkipGuestChecks to drop the guest checks rather than pretend to have made them.'
        }
        if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
            throw 'Stage 3 needs an SSH key: pass -SshPrivateKeyPath or -SshPrivateKey, or pass -SkipGuestChecks to drop the guest checks rather than pretend to have made them.'
        }
        Assert-OpenSshClient
    }

    # Ordered by controller/LUN, which is the order they were attached in, so
    # element 0 is the boot disk New-AerieVM.ps1 created the VM around. The
    # firmware's first boot device would be the pedantically correct source
    # but is a different object graph for the same answer.
    $drives = @(Get-VMHardDiskDrive -VMName $VMName | Sort-Object ControllerType, ControllerNumber, ControllerLocation)
    if ($drives.Count -eq 0) {
        throw "VM '$VMName' has no hard disks attached at all, which is not a state this script can say anything useful about."
    }

    $osDrive = $drives[0]
    $osVhd = Get-VHD -Path $osDrive.Path
    Write-Host "OS disk: $($osVhd.Path)"

    # ---------------------------------------------------------------- #
    Write-Stage 'Shape'
    # ---------------------------------------------------------------- #

    # --- the fixed disk ---

    if ($osVhd.VhdType -eq 'Fixed') {
        Add-Check -Step '3.6.disk' -Name 'OS disk is fixed' -Status 'Pass' -Detail "VhdType Fixed"
    }
    else {
        Add-Check -Step '3.6.disk' -Name 'OS disk is fixed' -Status 'Fail' -Detail "VhdType is $($osVhd.VhdType), want Fixed. This is the disk etcd's write-ahead log fsyncs to - see scripts/hyperv/README.md's on-disk layout rule. Move-NodeOsDisk.ps1 converts an existing node's"
    }

    $actualSizeGB = [math]::Round($osVhd.Size / 1GB, 1)
    if ([math]::Abs($actualSizeGB - $OsDiskSizeGB) -lt 1) {
        Add-Check -Step '3.6.disk' -Name 'OS disk virtual size' -Status 'Pass' -Detail "${actualSizeGB}GB"
    }
    else {
        $extra = if ($actualSizeGB -le 33) { ' 32GB is the old template default, which put every node root filesystem at 80-85% used - so this is almost certainly a resize that did not happen rather than a choice.' } else { '' }
        Add-Check -Step '3.6.disk' -Name 'OS disk virtual size' -Status 'Fail' -Detail "${actualSizeGB}GB, want ${OsDiskSizeGB}GB.$extra"
    }

    # The check that separates "declared fixed" from "written out". A disk can
    # report VhdType Fixed while a checkpoint layers a dynamic .avhdx over it,
    # and then the file the VM is actually writing to is not this one at all -
    # which is what the checkpoint checks below are for. This one is the
    # simpler question of whether the fixed file was allocated.
    $fileGB = [math]::Round($osVhd.FileSize / 1GB, 1)
    if ($osVhd.VhdType -ne 'Fixed') {
        Add-Check -Step '3.6.disk' -Name 'OS disk is allocated' -Status 'Fail' -Detail "${fileGB}GB on disk of ${actualSizeGB}GB virtual - not evaluated as a fill ratio because the disk is not fixed"
    }
    elseif ($osVhd.FileSize -ge ($osVhd.Size * $FixedDiskFillRatio)) {
        Add-Check -Step '3.6.disk' -Name 'OS disk is allocated' -Status 'Pass' -Detail "${fileGB}GB on disk of ${actualSizeGB}GB virtual"
    }
    else {
        Add-Check -Step '3.6.disk' -Name 'OS disk is allocated' -Status 'Fail' -Detail "${fileGB}GB on disk of ${actualSizeGB}GB virtual. A fixed VHDX is written out in full at creation; a sparse fraction means something converted it or it was never fixed"
    }

    # --- the data disk, present or deliberately absent ---

    $dataDrives = @($drives | Select-Object -Skip 1)
    if ($DataDiskSizeGB -eq 0) {
        if ($dataDrives.Count -eq 0) {
            Add-Check -Step '3.6.disk' -Name 'No data disk' -Status 'Pass' -Detail 'one disk attached, as asked'
        }
        else {
            Add-Check -Step '3.6.disk' -Name 'No data disk' -Status 'Fail' -Detail "$($dataDrives.Count) extra disk(s) attached: $(($dataDrives | ForEach-Object { $_.Path }) -join ', '). -DataDiskSizeGB 0 asserts the absence of one - on the part-time node that is finding 3, and the disk is doing nothing but occupying the host"
        }
    }
    elseif ($dataDrives.Count -eq 0) {
        Add-Check -Step '3.6.disk' -Name 'Data disk present' -Status 'Fail' -Detail "no second disk attached, want ${DataDiskSizeGB}GB. Longhorn has nothing to claim on this node"
    }
    else {
        $dataVhd = Get-VHD -Path $dataDrives[0].Path
        $dataGB = [math]::Round($dataVhd.Size / 1GB, 1)
        if ($dataVhd.VhdType -eq 'Fixed' -and [math]::Abs($dataGB - $DataDiskSizeGB) -lt 1) {
            Add-Check -Step '3.6.disk' -Name 'Data disk present' -Status 'Pass' -Detail "${dataGB}GB Fixed"
        }
        else {
            Add-Check -Step '3.6.disk' -Name 'Data disk present' -Status 'Fail' -Detail "${dataGB}GB $($dataVhd.VhdType), want ${DataDiskSizeGB}GB Fixed"
        }
    }

    # --- the checkpoint, which is the one whose absence is hardest to notice ---

    $snapshots = @(Get-VMSnapshot -VMName $VMName -ErrorAction SilentlyContinue)
    if ($snapshots.Count -eq 0) {
        Add-Check -Step '3.6.ckpt' -Name 'No checkpoints' -Status 'Pass' -Detail 'none'
    }
    else {
        Add-Check -Step '3.6.ckpt' -Name 'No checkpoints' -Status 'Fail' -Detail "$($snapshots.Count): $(($snapshots | ForEach-Object { $_.Name }) -join ', '). While one exists the guest writes to a dynamic .avhdx layered over the fixed OS disk, which is the entire cost the fixed disk removed. Move-NodeOsDisk.ps1 is what cleaned this off the three existing nodes"
    }

    # Guarded, like New-AerieVM.ps1's own write of it: the property does not
    # exist on Windows Server 2016's Hyper-V, where automatic checkpoints were
    # not a feature and so are not a problem either.
    if ($vm.PSObject.Properties['AutomaticCheckpointsEnabled']) {
        if (-not $vm.AutomaticCheckpointsEnabled) {
            Add-Check -Step '3.6.ckpt' -Name 'Automatic checkpoints off' -Status 'Pass' -Detail 'False'
        }
        else {
            Add-Check -Step '3.6.ckpt' -Name 'Automatic checkpoints off' -Status 'Fail' -Detail 'True. Hyper-V takes a checkpoint when the VM starts and removes it on a clean shutdown - so a node up for weeks, which is what a node is for, runs on a differencing disk the whole time. This was a default that caught all three existing nodes'
        }
    }
    else {
        Add-Check -Step '3.6.ckpt' -Name 'Automatic checkpoints off' -Status 'Pass' -Detail 'property absent - this Hyper-V predates the feature'
    }

    # --- memory, and why it is not Dynamic ---

    $memoryStartupGB = [math]::Round($vm.MemoryStartup / 1GB, 1)
    if (-not $vm.DynamicMemoryEnabled) {
        Add-Check -Step '3.6.mem' -Name 'Static memory' -Status 'Pass' -Detail "${memoryStartupGB}GB, dynamic memory off"
    }
    else {
        Add-Check -Step '3.6.mem' -Name 'Static memory' -Status 'Fail' -Detail 'Dynamic Memory is on. The kubelet reads allocatable memory once, at startup, from the memory present at boot - ballooning changes what the guest has without changing what the scheduler believes, and the OOM killer resolves the disagreement. docs/plans/part-time-node.md finding 5'
    }

    if ([math]::Abs($memoryStartupGB - $MemoryGB) -lt 0.5) {
        Add-Check -Step '3.6.mem' -Name 'Memory size' -Status 'Pass' -Detail "${memoryStartupGB}GB"
    }
    else {
        Add-Check -Step '3.6.mem' -Name 'Memory size' -Status 'Fail' -Detail "${memoryStartupGB}GB, want ${MemoryGB}GB"
    }

    # --- the start action (3.7) ---

    if ("$($vm.AutomaticStartAction)" -eq $AutomaticStartAction) {
        $why = if ($AutomaticStartAction -eq 'Nothing') { ' - the boot task reads the personal-mode state file and decides instead' } else { '' }
        Add-Check -Step '3.7.boot' -Name 'Automatic start action' -Status 'Pass' -Detail "$AutomaticStartAction$why"
    }
    else {
        $why = if ($AutomaticStartAction -eq 'Nothing') { ' A Windows Update reboot would bring this node back mid-session, which is the whole of finding 6 - Set-UpdateRebootSchedule.ps1 sets NoAutoRebootWithLoggedOnUsers = 0 deliberately, so that reboot goes straight through an active session.' } else { '' }
        Add-Check -Step '3.7.boot' -Name 'Automatic start action' -Status 'Fail' -Detail "$($vm.AutomaticStartAction), want $AutomaticStartAction.$why"
    }

    if ("$($vm.AutomaticStopAction)" -eq 'ShutDown') {
        Add-Check -Step '3.7.boot' -Name 'Automatic stop action' -Status 'Pass' -Detail 'ShutDown'
    }
    else {
        Add-Check -Step '3.7.boot' -Name 'Automatic stop action' -Status 'Fail' -Detail "$($vm.AutomaticStopAction), want ShutDown. Save/TurnOff both leave etcd to recover from a state it did not choose"
    }

    # --- the template's provenance: the one deliberate exception ---

    $provenance = Join-Path $TemplatePath 'debian-13-genericcloud.vhdx.provenance.json'
    $provenanceAlt = Get-ChildItem -Path $TemplatePath -Filter '*.vhdx.provenance.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not (Test-Path $provenance -PathType Leaf) -and $provenanceAlt) { $provenance = $provenanceAlt.FullName }

    if (Test-Path $provenance -PathType Leaf) {
        $prov = Get-Content $provenance -Raw | ConvertFrom-Json
        $provSize = if ($prov.PSObject.Properties['sizeGB']) { $prov.sizeGB } else { $null }
        $provFormat = if ($prov.PSObject.Properties['vhdxSubformat']) { $prov.vhdxSubformat } else { $null }
        $detail = "sizeGB $provSize, subformat $provFormat, built $(if ($prov.PSObject.Properties['builtUtc']) { $prov.builtUtc } else { 'unknown' })"

        # The template is dynamic on purpose - it is copied and never booted -
        # so `dynamic` here is the expected reading and `fixed` would be a
        # host paying for a full allocation of something nothing runs from.
        if ($provSize -eq $OsDiskSizeGB) {
            Add-Check -Step '3.6.tmpl' -Name 'Template provenance' -Status 'Pass' -Detail "$detail - built at the size this VM wanted, so the per-VM resize was a no-op here"
        }
        elseif ($null -ne $provSize) {
            Add-Check -Step '3.6.tmpl' -Name 'Template provenance' -Status 'Warn' -Detail "$detail - template is ${provSize}GB against this VM's ${OsDiskSizeGB}GB, so the per-VM resize is what produced the disk above. That is the supported path and the disk checks are the proof; noted because which of the two happened is worth recording"
        }
        else {
            Add-Check -Step '3.6.tmpl' -Name 'Template provenance' -Status 'Warn' -Detail "$detail - no sizeGB recorded, so this template predates the field"
        }
    }
    else {
        Add-Check -Step '3.6.tmpl' -Name 'Template provenance' -Status 'Warn' -Detail "no provenance file under '$TemplatePath'. Not a failure about this VM - the disk checks above already answer what it got - but the template's own history is unreadable here"
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Guest'
    # ---------------------------------------------------------------- #

    if ($SkipGuestChecks) {
        Write-Host '-SkipGuestChecks: the guest was not asked anything. The root filesystem may or may not have grown into the disk above.'
    }
    elseif ($vm.State -ne 'Running') {
        Add-Check -Step '3.8.guest' -Name 'Root filesystem grew' -Status 'Fail' -Detail "the VM is $($vm.State), so this cannot be evaluated. Start it and re-run, or pass -SkipGuestChecks to drop the guest checks deliberately"
    }
    else {
        $resolved = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName $VMName
        $tempKeyFile = $resolved.TempFile
        $knownHostsFile = Join-Path $env:TEMP "aerie-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"

        # One round trip for both answers, tagged so the parsing below does not
        # depend on line offsets in df's output.
        $probe = @'
echo '--- ROOTFS ---'
df -B1 --output=source,size,used,pcent / | tail -n 1
echo '--- LSBLK ---'
lsblk -b -o NAME,SIZE,TYPE,MOUNTPOINT
'@
        $result = Invoke-NodeSsh -IPAddress $IPAddress -User $Username -KeyPath $resolved.Path -KnownHostsFile $knownHostsFile -Command $probe

        if ($result.ExitCode -ne 0) {
            Add-Check -Step '3.8.guest' -Name 'Root filesystem grew' -Status 'Fail' -Detail "SSH to $Username@$IPAddress failed (exit $($result.ExitCode)): $($result.StdErr + $result.StdOut -replace '\s+', ' ')"
        }
        else {
            $rootLine = ($result.StdOut -split "`n" | Where-Object { $_ -match '^\s*/dev/' } | Select-Object -First 1)
            if (-not $rootLine) {
                Add-Check -Step '3.8.guest' -Name 'Root filesystem grew' -Status 'Fail' -Detail "df returned nothing this could read. Raw: $($result.StdErr + $result.StdOut -replace '\s+', ' ')"
            }
            else {
                $fields = @($rootLine -split '\s+' | Where-Object { $_ })
                $rootBytes = [int64]$fields[1]
                $rootGB = [math]::Round($rootBytes / 1GB, 1)

                # growpart takes the partition table, the EFI system partition
                # and filesystem metadata off the top, so the root filesystem
                # is always a little under the disk. 90% is generous enough
                # not to be a tuning knob and tight enough to separate "grew"
                # from "did not": a 100GB disk whose root is still the
                # template's 32GB reports 32%.
                if ($rootBytes -ge ($OsDiskSizeGB * 1GB * 0.90)) {
                    Add-Check -Step '3.8.guest' -Name 'Root filesystem grew' -Status 'Pass' -Detail "${rootGB}GB on / of a ${OsDiskSizeGB}GB disk ($($fields[3]) used)"
                }
                else {
                    Add-Check -Step '3.8.guest' -Name 'Root filesystem grew' -Status 'Fail' -Detail "${rootGB}GB on / of a ${OsDiskSizeGB}GB disk. growpart runs on first boot and expands root into whatever the disk turned out to be, so a root near the template size means the resize did not happen and -OsDiskSizeGB was silently ignored"
                }
            }

            # The data disk's absence, asserted from inside the guest as well
            # as from the host. Longhorn claims a raw unformatted disk, so
            # "the host attached none" and "the guest sees none" are the same
            # fact reached two ways, and disagreeing is worth knowing about.
            $lsblkSection = ($result.StdOut -split '--- LSBLK ---')[-1]
            $diskLines = @($lsblkSection -split "`n" | Where-Object { $_ -match '\bdisk\b' })
            if ($DataDiskSizeGB -eq 0 -and $diskLines.Count -gt 1) {
                Add-Check -Step '3.8.guest' -Name 'Guest block devices' -Status 'Fail' -Detail "$($diskLines.Count) whole disks visible in the guest, want 1: $(($diskLines | ForEach-Object { ($_ -split '\s+')[0] }) -join ', ')"
            }
            else {
                Add-Check -Step '3.8.guest' -Name 'Guest block devices' -Status 'Pass' -Detail "$($diskLines.Count) whole disk(s) visible, as expected"
            }
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
$elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalSeconds, 1)

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
        "## Node VM shape - $VMName - $(if ($failed -gt 0) { 'FAILED' } else { 'passed' })"
        ''
        "$passed passed, $warned warning(s), $failed failed, on $tick$env:COMPUTERNAME$tick in ${elapsed}s."
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
        '_Read-only. scripts/hyperv/README.md carries the on-disk layout rule these check against._'
    )
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "Node VM shape FAILED: $failed check(s) of $($script:Checks.Count) in ${elapsed}s." -ForegroundColor Red
    Write-Host 'Nothing was changed. scripts/hyperv/README.md has the layout rule; Move-NodeOsDisk.ps1 is what corrects a disk that is already wrong.'
    exit 1
}

Write-Host "Node VM shape passed: $($script:Checks.Count) check(s) in ${elapsed}s." -ForegroundColor Green
