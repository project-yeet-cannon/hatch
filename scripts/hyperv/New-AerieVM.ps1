<#
.SYNOPSIS
    Repeatably provisions a Hyper-V Linux VM from the golden image built by
    Get-GoldenImage.ps1 — the scratch VM for the Phase 0 DR-restore gate and
    the per-host node VMs in Phase 1 are the same shape, just different
    -RunCmd / -ExtraPackages payloads. See TODO_SWARM.md.

.DESCRIPTION
    Initialize-AerieNode.ps1 is the usual entry point - it preflights the
    host, builds the golden image if needed, calls this, and then verifies the
    result over SSH. Call this directly when you deliberately want to skip
    those checks.

    Per run, this:
      - copies the golden VHDX into a fresh per-VM OS disk (a full copy, not
        a differencing disk — so the golden template can be moved or deleted
        later without breaking VMs already built from it)
      - creates a second, fixed-size VHDX for Longhorn (Phase 1) / left
        unused (Phase 0 scratch VM)
      - renders a NoCloud cloud-init seed ISO with this VM's hostname/SSH
        key/NTP server/packages baked in, plus a break-glass console password
        for -Username (see -ConsolePassword)
      - creates a Generation 2 VM wired to an external switch with a fixed
        MAC address (so a DHCP reservation can be made ahead of first boot),
        MAC spoofing on (required for the CNI in Phase 2+), Secure Boot on
        the Microsoft UEFI CA template (needed for a shim-signed Linux
        guest), static memory (avoid ballooning on an etcd node), and the
        Hyper-V "Time Synchronization" integration service disabled so it
        can't fight the in-guest chrony/pfSense NTP config
      - starts the VM
      - if -LogIngestUrl/-LogIngestToken are supplied, registers a Scheduled
        Task that ships this VM's serial console to Aerie.Api's
        /api/vm-console-logs, from where it flows into OpenSearch alongside
        every other Aerie log line (see lib/Send-VmConsoleLog.ps1)

.EXAMPLE
    # Phase 0 scratch VM for the DR-restore gate
    .\New-AerieVM.ps1 -VMName aerie-dr-scratch -GoldenImagePath D:\aerie\vm-templates\debian-13-genericcloud.vhdx `
        -SwitchName ExternalSwitch -MacAddress 00-15-5D-01-02-03 `
        -SshPublicKeyPath ~\.ssh\id_ed25519.pub -NtpServer 10.0.0.1 `
        -ExtraPackages docker.io -DataDiskSizeGB 0

.EXAMPLE
    # Phase 1 node VM
    .\New-AerieVM.ps1 -VMName aerie-node-1 -GoldenImagePath D:\aerie\vm-templates\debian-13-genericcloud.vhdx `
        -SwitchName ExternalSwitch -MacAddress 00-15-5D-01-02-04 `
        -SshPublicKeyPath ~\.ssh\id_ed25519.pub -NtpServer 10.0.0.1 `
        -MemoryGB 16 -DataDiskSizeGB 200
#>
#Requires -Modules Hyper-V
#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'KeyPath')]
param(
    [Parameter(Mandatory)]
    [string]$VMName,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$GoldenImagePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Get-VMSwitch -Name $_ -ErrorAction SilentlyContinue })]
    [string]$SwitchName,

    # Fixed on purpose — TODO_SWARM.md Phase 1 calls for DHCP reservations
    # keyed to each VM's MAC, which only works if the MAC is known before
    # first boot rather than picked at random from Hyper-V's pool.
    [Parameter(Mandatory)]
    [ValidatePattern('^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$')]
    [string]$MacAddress,

    [Parameter(Mandatory, ParameterSetName = 'KeyPath')]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$SshPublicKeyPath,

    # The key material itself, for callers that hold it in a variable rather
    # than a file — GitHub Actions passes vars.NODE_SSH_PUBLIC_KEY straight
    # through instead of staging a temp file on the runner.
    [Parameter(Mandatory, ParameterSetName = 'KeyLiteral')]
    [ValidatePattern('^(ssh-(rsa|ed25519)|ecdsa-sha2-\S+)\s+\S+')]
    [string]$SshPublicKey,

    [Parameter(Mandatory)]
    [string]$NtpServer,

    [ValidateSet('Debian', 'Ubuntu')]
    [string]$Distro = 'Debian',

    [string]$Hostname = $VMName.ToLowerInvariant(),
    [string]$Domain,
    [string]$Username = 'aerie',

    [string]$VMStoragePath = 'D:\aerie\VMs',
    [int]$MemoryGB = 16,
    [int]$CPUCount = 4,

    # 0 skips the data disk entirely — the Phase 0 scratch VM doesn't need a
    # Longhorn disk, Phase 1 node VMs do.
    [int]$DataDiskSizeGB = 200,

    [string[]]$ExtraPackages = @(),
    [string[]]$RunCmd = @(),

    # Break-glass console login. Without it a VM that never reaches the
    # network can't be logged into at all - cloud-init locks every account's
    # password, so `vmconnect` gives you a login prompt with no credentials
    # that work, and screenshots of console scrollback are the only
    # diagnostic left. ssh_pwauth stays false, so this password is rejected
    # over SSH and is only usable at the Hyper-V console, which already
    # requires Administrator on the host. Pass '' to leave the account
    # password-locked and give up console access.
    [string]$ConsolePassword = 'password',

    # Both required together to ship this VM's serial console to OpenSearch
    # (see the Set-VMComPort block below) - omit either to skip it, e.g. for
    # the Phase 0 scratch VM.
    [string]$LogIngestUrl,
    [string]$LogIngestToken
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\New-NoCloudIso.ps1')
. (Join-Path $PSScriptRoot 'lib\Register-VmConsoleLogShipper.ps1')

if (Get-VM -Name $VMName -ErrorAction SilentlyContinue) {
    throw "A VM named '$VMName' already exists. Remove it first (Remove-VM -Name $VMName after stopping it) or pick a different name."
}

$macNormalized = ($MacAddress -replace '[:-]', '').ToUpperInvariant()
$existingMacs = Get-VM | Get-VMNetworkAdapter | Select-Object -ExpandProperty MacAddress
if ($existingMacs -contains $macNormalized) {
    throw "MAC $MacAddress is already assigned to another VM on this host. DHCP reservations depend on MACs being unique - pick another."
}

$vmDir = Join-Path $VMStoragePath $VMName
New-Item -ItemType Directory -Path $vmDir -Force | Out-Null

$osDiskPath = Join-Path $vmDir 'os-disk.vhdx'
Write-Host "Copying golden image to $osDiskPath ..."
Copy-Item -Path $GoldenImagePath -Destination $osDiskPath

if ($DataDiskSizeGB -gt 0) {
    $dataDiskPath = Join-Path $vmDir 'data-disk.vhdx'
    # TEMP: -Dynamic instead of -Fixed to skip upfront zeroing during workflow
    # iteration. Revert to -Fixed before provisioning a real node - see the
    # tradeoffs (I/O consistency under Longhorn, overcommit risk across the
    # three hosts) discussed in the provisioning chat.
    Write-Host "Creating ${DataDiskSizeGB}GB dynamic data disk at $dataDiskPath ..."
    New-VHD -Path $dataDiskPath -SizeBytes ([int64]$DataDiskSizeGB * 1GB) -Dynamic | Out-Null
}

# --- Render cloud-init seed ---

$seedSrc = Join-Path $vmDir 'seed-src'
New-Item -ItemType Directory -Path $seedSrc -Force | Out-Null

$sshKey = if ($SshPublicKey) { $SshPublicKey.Trim() } else { (Get-Content -Path $SshPublicKeyPath -Raw).Trim() }
$fqdn = if ($Domain) { "$Hostname.$Domain" } else { $Hostname }
$instanceId = [Guid]::NewGuid().ToString()

$hypervPackages = if ($Distro -eq 'Debian') { @('hyperv-daemons') } else { @('linux-tools-virtual', 'linux-cloud-tools-virtual') }
$packages = @('curl') + $hypervPackages + $ExtraPackages
$packagesBlock = ($packages | ForEach-Object { "  - $_" }) -join "`n"
$runcmdBlock = if ($RunCmd.Count -gt 0) { ($RunCmd | ForEach-Object { "  - $_" }) -join "`n" } else { "  - 'true'" }

# 'type: text' hands cloud-init the plaintext and lets it hash - without it
# cloud-init expects an already-hashed value and would set an unusable
# password. 'expire: false' keeps the account from demanding a password
# change at the console, which would defeat the point when you're locked out
# and debugging. Single-quoted YAML with doubled quotes so a password
# containing ':' or '#' can't break the document.
$consolePasswordBlock = if ($ConsolePassword) {
    $yamlPassword = "'" + ($ConsolePassword -replace "'", "''") + "'"
    @"
chpasswd:
  expire: false
  users:
    - name: $Username
      password: $yamlPassword
      type: text
"@
}
else { '' }

$userData = Get-Content -Path (Join-Path $PSScriptRoot 'cloud-init\user-data.tmpl.yaml') -Raw
$userData = $userData.Replace('__HOSTNAME__', $Hostname)
$userData = $userData.Replace('__FQDN__', $fqdn)
$userData = $userData.Replace('__USERNAME__', $Username)
$userData = $userData.Replace('__SSH_KEY__', $sshKey)
$userData = $userData.Replace('__PACKAGES__', $packagesBlock)
$userData = $userData.Replace('__NTP_SERVER__', $NtpServer)
$userData = $userData.Replace('__RUNCMD__', $runcmdBlock)
$userData = $userData.Replace('__CONSOLE_PASSWORD__', $consolePasswordBlock)
Set-Content -Path (Join-Path $seedSrc 'user-data') -Value $userData -NoNewline

$metaData = Get-Content -Path (Join-Path $PSScriptRoot 'cloud-init\meta-data.tmpl.yaml') -Raw
$metaData = $metaData.Replace('__INSTANCE_ID__', $instanceId).Replace('__HOSTNAME__', $Hostname)
Set-Content -Path (Join-Path $seedSrc 'meta-data') -Value $metaData -NoNewline

$isoPath = Join-Path $vmDir 'seed.iso'
New-NoCloudIso -SourceFolder $seedSrc -IsoPath $isoPath
Remove-Item -Path $seedSrc -Recurse -Force

# --- Create the VM ---

Write-Host "Creating VM '$VMName' ..."
New-VM -Name $VMName -Generation 2 -MemoryStartupBytes ([int64]$MemoryGB * 1GB) -VHDPath $osDiskPath -SwitchName $SwitchName -Path $VMStoragePath | Out-Null

Set-VMProcessor -VMName $VMName -Count $CPUCount
Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $false

if ($DataDiskSizeGB -gt 0) {
    Add-VMHardDiskDrive -VMName $VMName -Path $dataDiskPath -ControllerType SCSI
}

Add-VMDvdDrive -VMName $VMName -Path $isoPath

$osDisk = Get-VMHardDiskDrive -VMName $VMName | Where-Object { $_.Path -eq $osDiskPath }
Set-VMFirmware -VMName $VMName -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority -FirstBootDevice $osDisk

$nic = Get-VMNetworkAdapter -VMName $VMName
Set-VMNetworkAdapter -VMNetworkAdapter $nic -StaticMacAddress $macNormalized -MacAddressSpoofing On

Set-VM -Name $VMName -AutomaticStartAction Start -AutomaticStopAction ShutDown
Disable-VMIntegrationService -VMName $VMName -Name 'Time Synchronization'

# Set-VMComPort's -Path only accepts a named pipe ("\\.\pipe\Name") - Hyper-V
# has no built-in way to redirect a COM port straight to a file, on Gen 1 or
# Gen 2. A plain file path is accepted here without error but then fails
# Start-VM with a generic "parameter is incorrect" once vmwp.exe tries to
# bind it. Register-VmConsoleLogShipper (below) is what actually drains this
# pipe, when -LogIngestUrl/-LogIngestToken are supplied.
$comPipePath = "\\.\pipe\$VMName-com1"
Set-VMComPort -VMName $VMName -Number 1 -Path $comPipePath

Start-VM -Name $VMName

if ($LogIngestUrl -and $LogIngestToken) {
    Register-VmConsoleLogShipper -VMName $VMName -VmDir $vmDir -IngestUrl $LogIngestUrl -Token $LogIngestToken
    Write-Host "Console log shipper registered - serial console now flows to $LogIngestUrl (Scheduled Task 'Aerie-VMConsoleLog-$VMName')."
    Write-Host "  A copy is kept on this host at $(Join-Path $vmDir 'console.log') - read that when the VM never reaches the network."
}

Write-Host ""
Write-Host "VM '$VMName' started. MAC $MacAddress - register the DHCP reservation on pfSense now if it isn't already."
Write-Host "Cloud-init runs on first boot and reboots itself once when done; check progress with:"
Write-Host "  vmconnect localhost $VMName"
if ($ConsolePassword) {
    Write-Host "At that console, log in as '$Username' with the break-glass password (-ConsolePassword) if the VM never comes up on the network. SSH still refuses passwords."
}
Write-Host "Once it's up, confirm the DHCP lease matches the reservation and SSH in as '$Username'."
