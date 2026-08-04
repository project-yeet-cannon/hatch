<#
.SYNOPSIS
    Builds the golden VHDX template used by New-AerieVM.ps1: downloads the
    official Debian or Ubuntu generic cloud image (qcow2), converts it to a
    Hyper-V-native VHDX, and grows it so cloud-init's growpart module can
    expand the root filesystem into the extra space on first boot.

.DESCRIPTION
    Run this ONCE (per distro), not once per host — the output VHDX has no
    per-host state baked in. Copy the resulting file to every Hyper-V host
    that needs it (e.g. robocopy to each host's D:\vm-templates\), or run
    this script directly on each host if that's easier than moving a large
    file around.

    Requires qemu-img.exe to convert qcow2 -> vhdx; Hyper-V's own Convert-VHD
    only converts between VHD/VHDX, it doesn't read qcow2. Grab a Windows
    build from Cloudbase's qemu-img-windows page
    (https://cloudbase.it/qemu-img-windows/) and pass the downloaded zip's
    path via -QemuImgZipPath — deliberately not auto-downloaded here since
    release asset URLs are versioned and change.

.EXAMPLE
    .\Get-GoldenImage.ps1 -Distro Debian -QemuImgZipPath C:\Downloads\qemu-img-win-x64-2_3_0.zip -OutputPath D:\vm-templates\debian-13-genericcloud.vhdx
#>
#Requires -Modules Hyper-V
[CmdletBinding()]
param(
    [ValidateSet('Debian', 'Ubuntu')]
    [string]$Distro = 'Debian',

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$QemuImgZipPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    # Official vendor cloud-image URLs. These are stable, long-standing
    # locations, but point releases roll forward — override with -ImageUrl
    # if a download 404s and check the vendor's cloud-image page for the
    # current path.
    [string]$ImageUrl,

    [int]$SizeGB = 32
)

$ErrorActionPreference = 'Stop'

if (-not $ImageUrl) {
    $ImageUrl = switch ($Distro) {
        'Debian' { 'https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2' }
        'Ubuntu' { 'https://cloud-images.ubuntu.com/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img' }
    }
}

$work = Join-Path $env:TEMP "aerie-golden-image-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $sourceImage = Join-Path $work ([IO.Path]::GetFileName($ImageUrl))
    Write-Host "Downloading $ImageUrl ..."
    Invoke-WebRequest -Uri $ImageUrl -OutFile $sourceImage

    Write-Host "SHA256 of downloaded image (record this alongside the template for provenance):"
    Get-FileHash -Path $sourceImage -Algorithm SHA256 | Format-List

    $qemuImgDir = Join-Path $work 'qemu-img'
    Write-Host "Extracting qemu-img from $QemuImgZipPath ..."
    Expand-Archive -Path $QemuImgZipPath -DestinationPath $qemuImgDir

    $qemuImg = Get-ChildItem -Path $qemuImgDir -Filter 'qemu-img.exe' -Recurse | Select-Object -First 1
    if (-not $qemuImg) {
        throw "qemu-img.exe not found inside $QemuImgZipPath - check it's the qemu-img-windows release zip, not source."
    }

    $rawVhdx = Join-Path $work 'image-raw.vhdx'
    Write-Host "Converting qcow2 -> vhdx ..."
    & $qemuImg.FullName convert -O vhdx -o subformat=dynamic $sourceImage $rawVhdx
    if ($LASTEXITCODE -ne 0) { throw "qemu-img convert failed with exit code $LASTEXITCODE" }

    Write-Host "Growing image to ${SizeGB}GB (cloud-init's growpart module expands the root fs into this on first boot) ..."
    Resize-VHD -Path $rawVhdx -SizeBytes ([int64]$SizeGB * 1GB)

    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    Move-Item -Path $rawVhdx -Destination $OutputPath -Force

    Write-Host "Golden image ready: $OutputPath"
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
