<#
.SYNOPSIS
    End-to-end provisioning of one Aerie node VM on the Hyper-V host it's run
    from: preflight -> golden image -> VM -> wait for cloud-init -> report.

.DESCRIPTION
    This is the single entry point for TODO_SWARM.md Phase 1. The primary way
    to run it is dispatching .github/workflows/provision-0-new-node.yml - a
    thin wrapper that checks out the repo and invokes this script, holding no
    provisioning logic of its own so the two paths can't drift. Running it by
    hand in an elevated session on the host is the same code path, kept only
    as a fallback for when the runner isn't reachable.

    Everything needed lives under scripts\hyperv\, with no references outside
    it. Copying just that directory to a host is a supported way to run this.

    Stages:
      1. Preflight  - refuses to start unless the host, switch, MAC, name,
                      free space and target IP all check out. Cheap failures
                      before the expensive ones.
      2. Template   - builds the golden VHDX via Get-GoldenImage.ps1 if it
                      isn't already on this host. Skipped when present.
      3. VM         - delegates to New-AerieVM.ps1. Stages 2-3 are replaced by
                      a single Resume stage when a VM from a prior run of
                      this one is found in Preflight (see Idempotency below).
      4. Verify     - waits out cloud-init and its self-reboot over SSH, then
                      prints what actually came up. Skippable.

    Idempotency: an existing VM of the same name is never silently adopted or
    recreated, but it is resumed if its MAC matches -MacAddress - that's this
    same run's own VM continuing after an earlier failure (e.g. a cloud-init
    verify timeout), so the Template and Create stages are skipped, the VM is
    (re)started if it isn't running, and the run picks up at Verify. If the
    MAC doesn't match, it's an unrelated VM that happens to share the name,
    and that stays a hard error: `Remove-VM` it yourself or pick another
    name.

    A resumed VM's cloud-init config - including its SSH authorized_keys -
    was baked in at creation and is never re-applied, so a rotated
    -SshPublicKey/-SshPrivateKey (or NODE_SSH_PUBLIC_KEY/NODE_SSH_PRIVATE_KEY)
    silently has no effect on it: Verify will keep failing with the same
    "key offered wasn't accepted" error every retry. Pass -RecreateVM to stop
    and remove that stale VM and its disks and rebuild from scratch with
    today's inputs - the golden image template is untouched, so this doesn't
    repay the 10-20 min template build.

.PARAMETER ExpectedIPAddress
    The address the pfSense reservation maps -MacAddress to. Required unless
    -SkipWaitForReady, because verification is what proves the reservation
    was actually configured correctly - the single most common Phase 1
    failure, and invisible if you only check that the VM booted.

.EXAMPLE
    # Manual, on the host, in an elevated session
    .\Initialize-AerieNode.ps1 -VMName aerie-node-1 -MacAddress 00-15-5D-01-02-04 `
        -ExpectedIPAddress 10.0.0.21 -NtpServer 10.0.0.1 -Domain landis.family `
        -MemoryGB 16 -DataDiskSizeGB 200 `
        -SshPublicKeyPath ~\.ssh\id_ed25519.pub -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Check the host is ready without building anything
    .\Initialize-AerieNode.ps1 -VMName aerie-node-1 -MacAddress 00-15-5D-01-02-04 `
        -ExpectedIPAddress 10.0.0.21 -NtpServer 10.0.0.1 `
        -SshPublicKeyPath ~\.ssh\id_ed25519.pub -PreflightOnly
#>
#Requires -Modules Hyper-V
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$')]
    [string]$VMName,

    # Locally administered, Hyper-V's assigned OUI. TODO_SWARM.md Phase 1
    # wants DHCP reservations keyed to these, so they're chosen up front
    # rather than drawn from Hyper-V's dynamic pool. Convention is
    # 00-15-5D-<host>-<vm>-<nic>.
    [Parameter(Mandatory)]
    [ValidatePattern('^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$')]
    [string]$MacAddress,

    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$ExpectedIPAddress,

    [Parameter(Mandatory)]
    [string]$NtpServer,

    [ValidateSet('Debian', 'Ubuntu')]
    [string]$Distro = 'Debian',

    [string]$Domain,
    [string]$Username = 'aerie',

    [string]$SwitchName = 'ExternalSwitch',
    # Everything Aerie puts on a host's data volume lives under D:\aerie, so
    # the whole footprint is one directory to find, back up, or delete.
    [string]$VMStoragePath = 'D:\aerie\VMs',
    [string]$TemplatePath = 'D:\aerie\vm-templates',

    # Defaults to <TemplatePath>\<distro>.vhdx; override to share a template
    # from another volume or a UNC path.
    [string]$GoldenImagePath,

    [int]$MemoryGB = 16,
    [int]$CPUCount = 4,
    [int]$DataDiskSizeGB = 200,

    [string[]]$ExtraPackages = @(),
    [string[]]$RunCmd = @(),

    # Forwarded to New-AerieVM.ps1 - both required together to ship this
    # VM's serial console to OpenSearch. Omit either to skip it.
    [string]$LogIngestUrl,
    [string]$LogIngestToken,

    # Forwarded to New-AerieVM.ps1. Break-glass console login for -Username,
    # so a VM that never reaches the network is still debuggable from
    # `vmconnect` instead of only from screenshots of console scrollback.
    # Console-only - ssh_pwauth stays false. Pass '' to opt out.
    [string]$ConsolePassword = 'password',

    # Public half is injected by cloud-init; private half is used only to
    # verify the result and is never written to the VM.
    [string]$SshPublicKeyPath,
    [string]$SshPublicKey,
    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [switch]$SkipWaitForReady,
    [int]$ReadyTimeoutMinutes = 45,

    [switch]$PreflightOnly,

    # An existing VM with a matching MAC is normally resumed as-is (see
    # Idempotency above). This instead treats it as stale: stop it, remove
    # it, delete its disk directory, and rebuild fresh with today's inputs.
    # The one reason to reach for this is a rotated SSH key that a resumed
    # VM can never pick up on its own.
    [switch]$RecreateVM,

    # Forwarded to Get-GoldenImage.ps1, and only used when the template has
    # to be built on this host.
    [string]$QemuImgZipPath,
    [string]$QemuImgSha256,
    [switch]$SkipChecksumVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\AerieSsh.ps1')

$script:StageNumber = 0
function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

$tempKeyFile = $null
$privateKeyPath = $null
$knownHostsFile = $null
# Set-StrictMode is on, so anything referenced in the summary below has to
# exist even on the paths that never assign it.
$keyFingerprint = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $hostname = $VMName.ToLowerInvariant()
    $failures = New-Object Collections.Generic.List[string]

    # Resolve the key material first: everything else is cheap to check, but
    # a missing key only surfaces after the template build without this.
    if (-not $SshPublicKey -and -not $SshPublicKeyPath) {
        $failures.Add('No SSH public key: pass -SshPublicKeyPath or -SshPublicKey. Without it the VM boots with no way in.')
    }
    elseif (-not $SshPublicKey) {
        if (-not (Test-Path $SshPublicKeyPath -PathType Leaf)) {
            $failures.Add("SSH public key not found at '$SshPublicKeyPath'.")
        }
        else {
            $SshPublicKey = (Get-Content -Path $SshPublicKeyPath -Raw).Trim()
        }
    }
    if ($SshPublicKey -and $SshPublicKey -notmatch '^(ssh-(rsa|ed25519)|ecdsa-sha2-\S+)\s+\S+') {
        $failures.Add("The SSH public key doesn't look like an OpenSSH public key. Did a private key get passed by mistake?")
    }

    $waiting = -not $SkipWaitForReady
    if ($waiting) {
        if (-not $ExpectedIPAddress) {
            $failures.Add('-ExpectedIPAddress is required unless -SkipWaitForReady. It is the address the pfSense reservation maps -MacAddress to, and checking it is how this run proves the reservation works.')
        }
        if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
            $failures.Add('Verification needs the private key: pass -SshPrivateKeyPath or -SshPrivateKey, or run with -SkipWaitForReady.')
        }
        $privateKeyMaterialFound = $SshPrivateKey -or ($SshPrivateKeyPath -and (Test-Path $SshPrivateKeyPath -PathType Leaf))
        if ($SshPrivateKeyPath -and -not $SshPrivateKey -and -not (Test-Path $SshPrivateKeyPath -PathType Leaf)) {
            $failures.Add("SSH private key not found at '$SshPrivateKeyPath'.")
        }
        $opensshOk = $true
        try { Assert-OpenSshClient } catch { $opensshOk = $false; $failures.Add($_.Exception.Message) }

        # Resolved here rather than in Verify: a malformed key should fail
        # preflight in seconds, not after a full golden-image build and VM
        # boot. $tempKeyFile is cleaned up in the top-level `finally` no
        # matter which stage a later failure happens in.
        if ($privateKeyMaterialFound -and $opensshOk) {
            try {
                # -ExpectedPublicKey makes this the pairing check too: the
                # public half is what gets baked into the VM, so a private
                # key that doesn't correspond to it can only ever be refused.
                $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath `
                    -ExpectedPublicKey $SshPublicKey -VMName $VMName
                $privateKeyPath = $resolvedKey.Path
                $tempKeyFile = $resolvedKey.TempFile
                $keyFingerprint = $resolvedKey.Fingerprint
            }
            catch {
                $failures.Add($_.Exception.Message)
            }
        }
    }

    # Switch: existence alone isn't enough. An Internal or Private switch
    # would let the VM build and boot, then leave it unreachable from the LAN
    # with no DHCP - a failure that looks like a DHCP problem for an hour.
    $vmSwitch = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    if (-not $vmSwitch) {
        $failures.Add("No virtual switch named '$SwitchName' on $env:COMPUTERNAME. Create one bound to a physical NIC:`n      Get-NetAdapter -Physical | Where-Object Status -eq 'Up'`n      New-VMSwitch -Name $SwitchName -NetAdapterName '<adapter>' -AllowManagementOS `$true")
    }
    elseif ($vmSwitch.SwitchType -ne 'External') {
        $failures.Add("Switch '$SwitchName' is $($vmSwitch.SwitchType), not External. Phase 1 needs each VM on the LAN with its own DHCP-reserved address; an $($vmSwitch.SwitchType) switch can't get one.")
    }

    $macNormalized = ($MacAddress -replace '[:-]', '').ToUpperInvariant()
    if (-not $macNormalized.StartsWith('00155D')) {
        Write-Warning "MAC $MacAddress isn't in Hyper-V's 00-15-5D OUI. That's legal, but the convention keeps these distinguishable from physical NICs on the LAN."
    }

    # A VM of this name is only ever resumed or (with -RecreateVM) rebuilt,
    # never silently adopted: if its MAC matches, it's this same run's own VM
    # continuing after an earlier failure (Template/Create already happened),
    # so the rest of preflight and the stages below either resume it as-is or
    # tear it down and rebuild it, rather than treating it as a conflict. If
    # the MAC doesn't match, it's an unrelated VM that happens to share the
    # name, and that's still a hard error regardless of -RecreateVM.
    $existingVM = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    $resuming = $false
    $recreating = $false
    if ($existingVM) {
        $existingVmMacs = @($existingVM | Get-VMNetworkAdapter | Select-Object -ExpandProperty MacAddress)
        if ($existingVmMacs -notcontains $macNormalized) {
            $failures.Add("A VM named '$VMName' already exists on this host, but its MAC ($($existingVmMacs -join ', ')) doesn't match the requested $MacAddress - this looks like an unrelated VM, not a resumable run of this one. Stop and remove it first (Stop-VM -Name $VMName -TurnOff; Remove-VM -Name $VMName) or pick another name.")
        }
        elseif ($RecreateVM) {
            $recreating = $true
        }
        else {
            $resuming = $true
        }
    }

    $existingMacs = @(Get-VM | Where-Object { $_.Name -ne $VMName } | Get-VMNetworkAdapter | Select-Object -ExpandProperty MacAddress)
    if ($existingMacs -contains $macNormalized) {
        $failures.Add("MAC $MacAddress is already assigned to another VM on this host. DHCP reservations depend on MACs being unique.")
    }

    # An answer here means something already holds the address the reservation
    # points at, so the VM would either not get it or collide with a live
    # host. Silence proves nothing (ICMP may be filtered), so this only ever
    # fails on a positive response - except when resuming or recreating,
    # where our own (soon to be torn down, in the recreate case) VM answering
    # is exactly what should happen.
    if (-not $resuming -and -not $recreating -and $ExpectedIPAddress -and (Test-Connection $ExpectedIPAddress -Count 2 -Quiet -ErrorAction SilentlyContinue)) {
        $failures.Add("$ExpectedIPAddress already answers ping, so it isn't free. Either the reservation points at an in-use address, or a previous attempt at this VM is still running somewhere. (New-AerieVM.ps1 can be run directly to bypass this check if you know better.)")
    }

    if (-not $GoldenImagePath) {
        $imageFile = if ($Distro -eq 'Debian') { 'debian-13-genericcloud.vhdx' } else { 'ubuntu-24.04-server-cloudimg.vhdx' }
        $GoldenImagePath = Join-Path $TemplatePath $imageFile
    }

    # Free space is only needed for a fresh OS disk copy + data disk; a
    # resumed VM already has both, and by now may well have consumed the
    # margin this check would otherwise demand again. A recreate also needs
    # this - its old disks aren't deleted until after Preflight returns, so
    # this conservatively counts a full new copy on top of the old one still
    # on disk rather than assuming the teardown that hasn't happened yet.
    if (-not $resuming) {
        # Free space: the OS disk is a full copy of the template (not a
        # differencing disk), and the data disk is fixed, so both consume
        # their full size immediately. Running out mid-copy leaves a
        # half-built VM.
        $templateSizeBytes = if (Test-Path $GoldenImagePath) { (Get-Item $GoldenImagePath).Length } else { 32GB }
        $neededBytes = $templateSizeBytes + ([int64]$DataDiskSizeGB * 1GB) + 2GB

        # -FilePath needs the path to exist, which it won't on a first run,
        # so fall back to the drive letter the path is rooted at.
        $volume = Get-Volume -FilePath $VMStoragePath -ErrorAction SilentlyContinue
        if (-not $volume) {
            $driveLetter = [IO.Path]::GetPathRoot($VMStoragePath).TrimEnd('\', ':')
            if ($driveLetter) { $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction SilentlyContinue }
        }
        if (-not $volume) {
            $failures.Add("Can't inspect the volume for -VMStoragePath '$VMStoragePath'. Does that drive exist on $env:COMPUTERNAME?")
        }
        elseif ($volume.SizeRemaining -lt $neededBytes) {
            $failures.Add("Not enough free space on $($volume.DriveLetter): - need ~$([math]::Round($neededBytes/1GB,1))GB (OS disk copy + ${DataDiskSizeGB}GB fixed data disk), have $([math]::Round($volume.SizeRemaining/1GB,1))GB.")
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed on $env:COMPUTERNAME with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Host:      $env:COMPUTERNAME"
    Write-Host "VM:        $VMName ($MemoryGB GB RAM, $CPUCount vCPU, ${DataDiskSizeGB}GB data disk)"
    Write-Host "MAC:       $MacAddress  ->  switch '$SwitchName' (External)"
    Write-Host "Expecting: $(if ($ExpectedIPAddress) { $ExpectedIPAddress } else { '(not verifying - -SkipWaitForReady)' })"
    Write-Host "Template:  $GoldenImagePath"
    # Printed so a later auth failure can be checked against something rather
    # than guessed at: this same fingerprint appears in the seed ISO's
    # user-data and in cloud-init's authorized-keys banner on the VM console.
    if ($keyFingerprint) {
        Write-Host "SSH key:   $keyFingerprint (public key matches the private key verification will use)"
    }
    if ($resuming) {
        Write-Host "Mode:      RESUMING - VM '$VMName' already exists with matching MAC $MacAddress; Template/Create will be skipped."
    }
    elseif ($recreating) {
        Write-Host "Mode:      RECREATING - VM '$VMName' already exists with matching MAC $MacAddress; -RecreateVM will stop and remove it (and delete its disks) before rebuilding fresh."
    }
    Write-Host 'Preflight OK.'

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here without creating anything.' -ForegroundColor Yellow
        return
    }

    if ($recreating) {
        # ---------------------------------------------------------------- #
        Write-Stage 'Recreate: removing stale VM'
        # ---------------------------------------------------------------- #

        Write-Warning "-RecreateVM: treating VM '$VMName' as stale (e.g. its baked-in cloud-init config predates a rotated SSH key) rather than resuming it. Stopping it, removing it, and deleting its disk directory before rebuilding from scratch with today's inputs."
        if ((Get-VM -Name $VMName).State -ne 'Off') {
            Write-Host "Stopping VM '$VMName' ..."
            Stop-VM -Name $VMName -TurnOff -Force
        }
        Remove-VM -Name $VMName -Force
        $staleVmDir = Join-Path $VMStoragePath $VMName
        if (Test-Path $staleVmDir) {
            Write-Host "Deleting $staleVmDir ..."
            Remove-Item -Path $staleVmDir -Recurse -Force
        }
    }

    if ($resuming) {
        # ---------------------------------------------------------------- #
        Write-Stage 'Resuming existing VM'
        # ---------------------------------------------------------------- #

        Write-Host "Found VM '$VMName' with matching MAC from a previous run of this script - skipping Golden image and Create, picking up at Verify."
        Write-Warning "This VM's cloud-init config (SSH key, packages, hostname, etc.) was baked in by the run that created it and is NOT re-applied now. If -SshPublicKey or other inputs changed since then - including a rotated NODE_SSH_PUBLIC_KEY/NODE_SSH_PRIVATE_KEY - this run verifies against what's already on the VM, not against today's inputs. Re-run with -RecreateVM to stop, remove, and rebuild it with today's inputs."
        $currentState = (Get-VM -Name $VMName).State
        if ($currentState -eq 'Running') {
            Write-Host "VM is already running."
        }
        else {
            Write-Host "VM is $currentState - starting it."
            Start-VM -Name $VMName
        }
    }
    else {
        # ---------------------------------------------------------------- #
        Write-Stage 'Golden image'
        # ---------------------------------------------------------------- #

        if (Test-Path $GoldenImagePath) {
            Write-Host "Already present, reusing: $GoldenImagePath"
        }
        else {
            Write-Host "Not on this host - building it (one-time, ~10-20 min depending on link speed)."
            $goldenArgs = @{
                Distro     = $Distro
                OutputPath = $GoldenImagePath
            }
            if ($QemuImgZipPath) { $goldenArgs.QemuImgZipPath = $QemuImgZipPath }
            if ($QemuImgSha256) { $goldenArgs.QemuImgSha256 = $QemuImgSha256 }
            if ($SkipChecksumVerification) { $goldenArgs.SkipChecksumVerification = $true }
            & (Join-Path $PSScriptRoot 'Get-GoldenImage.ps1') @goldenArgs
        }

        # ---------------------------------------------------------------- #
        Write-Stage 'Create and start the VM'
        # ---------------------------------------------------------------- #

        $vmArgs = @{
            VMName          = $VMName
            GoldenImagePath = $GoldenImagePath
            SwitchName      = $SwitchName
            MacAddress      = $MacAddress
            SshPublicKey    = $SshPublicKey
            NtpServer       = $NtpServer
            Distro          = $Distro
            Username        = $Username
            VMStoragePath   = $VMStoragePath
            MemoryGB        = $MemoryGB
            CPUCount        = $CPUCount
            DataDiskSizeGB  = $DataDiskSizeGB
        }
        if ($Domain) { $vmArgs.Domain = $Domain }
        if ($ExtraPackages) { $vmArgs.ExtraPackages = $ExtraPackages }
        if ($RunCmd) { $vmArgs.RunCmd = $RunCmd }
        if ($LogIngestUrl -and $LogIngestToken) {
            $vmArgs.LogIngestUrl = $LogIngestUrl
            $vmArgs.LogIngestToken = $LogIngestToken
        }
        # Passed explicitly rather than left to New-AerieVM.ps1's own default,
        # so -ConsolePassword '' here actually opts out instead of silently
        # picking the default up again one script down.
        $vmArgs.ConsolePassword = $ConsolePassword

        if ($DataDiskSizeGB -gt 0) {
            Write-Host "Note: the ${DataDiskSizeGB}GB data disk is fixed-size, so Hyper-V zeroes it up front. Expect this to take a while on spinning storage."
        }
        & (Join-Path $PSScriptRoot 'New-AerieVM.ps1') @vmArgs
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    if ($SkipWaitForReady) {
        Write-Host '-SkipWaitForReady: not waiting for cloud-init.'
        Write-Host "Watch it yourself with:  vmconnect localhost $VMName"
        $nodeReport = $null
    }
    else {
        # $privateKeyPath was already resolved and format-checked in
        # Preflight (see Resolve-SshPrivateKeyFile) - preflight would have
        # thrown before we ever got here if it didn't parse.

        # A throwaway known_hosts: this VM is brand new, so any key already
        # recorded for that IP belongs to something else and would only
        # produce a spurious host-key-changed failure.
        $knownHostsFile = Join-Path $env:TEMP "aerie-$VMName-$([Guid]::NewGuid().ToString('N')).known_hosts"

        if ($ConsolePassword) {
            Write-Host "If this never comes up, log in at 'vmconnect localhost $VMName' as '$Username' with the break-glass console password and read /var/log/cloud-init-output.log."
        }

        $nodeReport = Wait-AerieNodeReady `
            -IPAddress $ExpectedIPAddress `
            -User $Username `
            -KeyPath $privateKeyPath `
            -KnownHostsFile $knownHostsFile `
            -ExpectedHostname $VMName `
            -TimeoutMinutes $ReadyTimeoutMinutes

        Write-Host ''
        Write-Host $nodeReport.Report

        # The reservation is the thing most likely to be wrong, so confirm the
        # machine answering at that address is actually the one just built
        # rather than assuming a reachable host is the right host.
        if ($nodeReport.Report -notmatch [regex]::Escape($hostname)) {
            throw "Something answered at $ExpectedIPAddress but didn't identify as '$hostname'. That usually means the DHCP reservation for $MacAddress points somewhere else, or another host holds this address."
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "Node '$VMName' provisioned on $env:COMPUTERNAME in ${elapsed} min." -ForegroundColor Green
    if (-not $SkipWaitForReady) {
        Write-Host "  ssh $Username@$ExpectedIPAddress"
    }
    Write-Host ''
    Write-Host 'Phase 1 checklist for this node - confirm and tick off in TODO_SWARM.md:'
    Write-Host '  - external switch, DHCP-reserved LAN address    (verified above)'
    Write-Host '  - MAC spoofing on the vNIC                      (set by New-AerieVM.ps1)'
    Write-Host '  - second fixed VHDX for Longhorn                ' -NoNewline
    Write-Host $(if ($DataDiskSizeGB -gt 0) { "(${DataDiskSizeGB}GB, unformatted - Longhorn claims it in Phase 3)" } else { '(SKIPPED - -DataDiskSizeGB 0)' })
    Write-Host '  - autostart + Shut Down stop action             (set by New-AerieVM.ps1)'
    Write-Host '  - NTP from pfSense                              ' -NoNewline
    Write-Host "($NtpServer - see chrony output above)"
    Write-Host '  - stagger Windows Update reboots across hosts   ' -NoNewline
    Write-Host '(run the "Stagger Windows Update reboots" workflow now that this node exists)'

    # A no-op outside Actions, which is the point: the manual and CI paths run
    # the same script and only differ in whether this variable is set.
    if ($env:GITHUB_STEP_SUMMARY) {
        # Backticks are PowerShell's escape character, so markdown's code
        # fences and inline code spans are far easier to read as variables
        # than as doubled-up escapes inside the here-string.
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $address = if ($ExpectedIPAddress) { "$tick$ExpectedIPAddress$tick" } else { '_not verified_' }

        $lines = @(
            "## Node $tick$VMName$tick provisioned"
            ''
            '| | |'
            '|---|---|'
            "| Hyper-V host | $tick$env:COMPUTERNAME$tick |"
            "| MAC | $tick$MacAddress$tick |"
            "| Address | $address |"
            "| Distro | $Distro |"
            "| Resources | ${MemoryGB}GB RAM, $CPUCount vCPU, ${DataDiskSizeGB}GB data disk |"
            "| Elapsed | ${elapsed} min |"
            ''
        )
        if ($nodeReport) {
            $lines += @(
                '<details><summary>Node report</summary>'
                ''
                $fence
                $nodeReport.Report.TrimEnd()
                $fence
                ''
                '</details>'
            )
        }
        else {
            $lines += "Provisioned with $tick-SkipWaitForReady$tick; cloud-init was not verified."
        }
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
}
