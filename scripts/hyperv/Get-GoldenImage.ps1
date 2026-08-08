<#
.SYNOPSIS
    Builds the golden VHDX template used by New-AerieVM.ps1: downloads the
    official Debian or Ubuntu generic cloud image (qcow2), converts it to a
    Hyper-V-native VHDX, and grows it so cloud-init's growpart module can
    expand the root filesystem into the extra space on first boot.

.DESCRIPTION
    Run this ONCE (per distro), not once per host — the output VHDX has no
    per-host state baked in. Copy the resulting file to every Hyper-V host
    that needs it (e.g. robocopy to each host's D:\aerie\vm-templates\), or run
    this script directly on each host if that's easier than moving a large
    file around. Initialize-AerieNode.ps1 calls this automatically when the
    template is missing.

    Requires qemu-img.exe to convert qcow2 -> vhdx; Hyper-V's own Convert-VHD
    only converts between VHD/VHDX, it doesn't read qcow2. With no qemu-img
    arguments this downloads the pinned Cloudbase build (-QemuImgUrl); pass
    -QemuImgZipPath to use a zip you downloaded yourself instead.

.NOTES
    Integrity checking is deliberately asymmetric, because the two upstreams
    differ in what they publish:

    - The distro image is verified against the vendor's own SHA512SUMS /
      SHA256SUMS file, fetched from the same directory. Fully automatic, and
      it fails the run on mismatch.
    - Cloudbase publishes no checksum file, so qemu-img gets trust-on-first-
      use pinning instead: the first run prints the hash, and passing it back
      as -QemuImgSha256 (the workflow reads vars.QEMU_IMG_SHA256) verifies it
      from then on. Without that variable the download is unverified — which
      is why the script says so loudly rather than quietly proceeding.

.EXAMPLE
    # Fully automatic — downloads a pinned qemu-img build
    .\Get-GoldenImage.ps1 -Distro Debian -OutputPath D:\aerie\vm-templates\debian-13-genericcloud.vhdx

.EXAMPLE
    # Using a qemu-img zip downloaded by hand, with the image hash pinned
    .\Get-GoldenImage.ps1 -Distro Debian -QemuImgZipPath C:\Downloads\qemu-img-win-x64-2_3_0.zip `
        -QemuImgSha256 A1B2... -OutputPath D:\aerie\vm-templates\debian-13-genericcloud.vhdx
#>
#Requires -Modules Hyper-V
[CmdletBinding()]
param(
    [ValidateSet('Debian', 'Ubuntu')]
    [string]$Distro = 'Debian',

    [Parameter(Mandatory)]
    [string]$OutputPath,

    # Official vendor cloud-image URLs. These are stable, long-standing
    # locations, but point releases roll forward — override with -ImageUrl
    # if a download 404s and check the vendor's cloud-image page for the
    # current path.
    [string]$ImageUrl,

    # A qemu-img-windows zip already on disk. Takes precedence over
    # -QemuImgUrl; supply it when the host has no internet path to
    # cloudbase.it, or when you want a specific build.
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$QemuImgZipPath,

    # Cloudbase's release assets are versioned, so this is pinned rather than
    # resolved to "latest" — a silently-newer converter is exactly the kind
    # of thing that turns a reproducible template into an irreproducible one.
    # If it 404s, check https://cloudbase.it/qemu-img-windows/ and bump it.
    [string]$QemuImgUrl = 'https://cloudbase.it/downloads/qemu-img-win-x64-2_3_0.zip',

    # See .NOTES — trust-on-first-use pin for the qemu-img zip.
    [string]$QemuImgSha256,

    [int]$SizeGB = 32
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 renders a progress bar for every chunk Invoke-WebRequest
# receives, which costs far more than the download itself on a ~400MB image
# (minutes vs. seconds). Also pin TLS 1.2 — 5.1 still negotiates SSL3/TLS1.0
# by default on some Server SKUs, which these hosts reject outright.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $ImageUrl) {
    $ImageUrl = switch ($Distro) {
        'Debian' { 'https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2' }
        'Ubuntu' { 'https://cloud-images.ubuntu.com/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img' }
    }
}

# Both vendors publish a sums file next to the images; only the algorithm and
# filename differ.
$checksumFile = if ($Distro -eq 'Debian') { 'SHA512SUMS' } else { 'SHA256SUMS' }
$checksumAlgo = if ($Distro -eq 'Debian') { 'SHA512' } else { 'SHA256' }
$checksumUrl = ($ImageUrl -replace '/[^/]+$', "/$checksumFile")

$work = Join-Path $env:TEMP "aerie-golden-image-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $imageName = [IO.Path]::GetFileName($ImageUrl)
    $sourceImage = Join-Path $work $imageName

    Write-Host "Downloading $ImageUrl ..."
    Invoke-WebRequest -Uri $ImageUrl -OutFile $sourceImage -UseBasicParsing

    # The vendor regenerates the image files and the sums file for "latest"
    # as a batch, then syncs them out; that sync isn't atomic from a reader's
    # perspective, so a request can land mid-rebuild and see a sums file
    # that's momentarily empty/partial and missing our entry. Retry a few
    # times before concluding the image was actually renamed.
    $maxAttempts = 5
    $expected = $null
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        Write-Host "Verifying against $checksumUrl ... (attempt $attempt/$maxAttempts)"
        $sums = (Invoke-WebRequest -Uri $checksumUrl -UseBasicParsing).Content

        # Both formats are "<hash><whitespace>[*]<filename>"; the leading '*'
        # is coreutils' binary-mode marker, which Ubuntu emits and Debian
        # doesn't.
        foreach ($line in ($sums -split "`r?`n")) {
            if ($line -match '^([0-9a-fA-F]+)\s+\*?(.+)$' -and $Matches[2].Trim() -eq $imageName) {
                $expected = $Matches[1].ToLowerInvariant()
                break
            }
        }

        if ($expected) { break }
        if ($attempt -lt $maxAttempts) {
            Write-Warning "No $checksumAlgo entry for '$imageName' yet - the vendor's 'latest' listing may be mid-rebuild. Retrying in 15s..."
            Start-Sleep -Seconds 15
        }
    }
    if (-not $expected) {
        throw "No $checksumAlgo entry for '$imageName' in $checksumUrl after $maxAttempts attempts. The vendor may have renamed the image - check the cloud-image page and pass -ImageUrl explicitly."
    }

    $actual = (Get-FileHash -Path $sourceImage -Algorithm $checksumAlgo).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "$checksumAlgo mismatch for $imageName.`n  expected: $expected`n  actual:   $actual`nRefusing to build a template from an image that doesn't match the vendor's published hash."
    }
    Write-Host "  OK - $checksumAlgo matches the vendor's published hash."

    # --- qemu-img ---

    if ($QemuImgZipPath) {
        $zip = $QemuImgZipPath
        Write-Host "Using qemu-img zip: $zip"
    }
    else {
        $zip = Join-Path $work 'qemu-img.zip'
        Write-Host "Downloading qemu-img from $QemuImgUrl ..."
        Invoke-WebRequest -Uri $QemuImgUrl -OutFile $zip -UseBasicParsing
    }

    $zipHash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($QemuImgSha256) {
        if ($zipHash -ne $QemuImgSha256.ToLowerInvariant()) {
            throw "qemu-img zip SHA256 mismatch.`n  expected: $($QemuImgSha256.ToLowerInvariant())`n  actual:   $zipHash"
        }
        Write-Host "  OK - qemu-img zip matches the pinned SHA256."
    }
    else {
        Write-Warning "qemu-img zip is UNVERIFIED (no -QemuImgSha256 given). Its SHA256 is:`n  $zipHash`nPin it - set repository variable QEMU_IMG_SHA256 to that value - so later runs are verified."
    }

    $qemuImgDir = Join-Path $work 'qemu-img'
    Expand-Archive -Path $zip -DestinationPath $qemuImgDir

    $qemuImg = Get-ChildItem -Path $qemuImgDir -Filter 'qemu-img.exe' -Recurse | Select-Object -First 1
    if (-not $qemuImg) {
        throw "qemu-img.exe not found inside $zip - check it's the qemu-img-windows release zip, not source."
    }

    # --- convert and grow ---

    $rawVhdx = Join-Path $work 'image-raw.vhdx'
    Write-Host "Converting qcow2 -> vhdx ..."
    & $qemuImg.FullName convert -O vhdx -o subformat=dynamic $sourceImage $rawVhdx
    if ($LASTEXITCODE -ne 0) { throw "qemu-img convert failed with exit code $LASTEXITCODE" }

    Write-Host "Growing image to ${SizeGB}GB (cloud-init's growpart module expands the root fs into this on first boot) ..."
    Resize-VHD -Path $rawVhdx -SizeBytes ([int64]$SizeGB * 1GB)

    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    Move-Item -Path $rawVhdx -Destination $OutputPath -Force

    # Recorded next to the template so a host can later be asked "which image
    # is this VM's lineage?" without re-downloading anything.
    $provenance = [ordered]@{
        distro        = $Distro
        imageUrl      = $ImageUrl
        imageName     = $imageName
        imageChecksum = "${checksumAlgo}:$expected"
        qemuImgSource = if ($QemuImgZipPath) { $QemuImgZipPath } else { $QemuImgUrl }
        qemuImgSha256 = $zipHash
        sizeGB        = $SizeGB
        builtUtc      = (Get-Date).ToUniversalTime().ToString('o')
        builtOn       = $env:COMPUTERNAME
    }
    $provenance | ConvertTo-Json | Set-Content -Path "$OutputPath.provenance.json"

    Write-Host "Golden image ready: $OutputPath"
    Write-Host "Provenance:         $OutputPath.provenance.json"
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
