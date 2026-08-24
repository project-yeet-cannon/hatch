<#
.SYNOPSIS
    Shared helpers for the scripts that prepare a k3s node's disks over SSH:
    staged console output, defensive JSON field access, and the two parsers
    that turn one combined probe into something to reason about.

.DESCRIPTION
    Extracted from Initialize-NodeStorage.ps1 when Add-BulkDisk.ps1 arrived
    needing the identical four functions. They are pure - nothing here opens
    a connection or touches a node - which is what makes the extraction safe:
    both callers get the same parsing, and a fix to ConvertTo-DeviceList's
    handling of a nested block device is a fix in both places at once.

    Dot-source it, the way both callers dot-source ..\hyperv\lib\AerieSsh.ps1:

        . (Join-Path $PSScriptRoot 'lib\AerieNodeDisk.ps1')

    Dot-sourcing runs in the caller's scope, so $script:StageNumber below
    initialises the *caller's* stage counter and Write-Stage increments that
    one. Two scripts in one session therefore do not share a counter, which
    is the behaviour each of them had when it owned its own copy.
#>

$script:StageNumber = 0

function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Get-Field {
    <#
    .SYNOPSIS
        Reads a property off a ConvertFrom-Json object, returning $null when
        it's absent instead of throwing under Set-StrictMode.
    #>
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name
    )
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ProbeSection {
    <#
    .SYNOPSIS
        Pulls one '--- name' section out of the combined probe output.

    .DESCRIPTION
        One SSH round trip collects everything the inspect stage reasons
        about, rather than eight. That matters less for speed than for
        consistency: eight round trips are eight moments at which the node
        could change under the decision being made about it, and "the disk
        was empty when I looked and had a filesystem when I wrote to it" is
        not a race worth leaving open in a script whose next action is mkfs.
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

function ConvertTo-DeviceList {
    <#
    .SYNOPSIS
        Flattens `lsblk -J`'s nested blockdevices into one list, tagging every
        entry with the top-level disk it belongs to.

    .DESCRIPTION
        The nesting is the interesting part and is easiest to reason about
        flattened: "is this disk in use" is a question about the whole subtree
        under it (a mounted partition, an LVM member, a LUKS container), not
        about the disk node itself, whose own fstype and mountpoint are empty
        in every one of those cases.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Json)

    $devices = New-Object Collections.Generic.List[psobject]
    if ([string]::IsNullOrWhiteSpace($Json)) { return $devices }

    $parsed = $Json | ConvertFrom-Json
    # Where-Object, not a bare @(): an absent key gives $null, and @($null) is
    # a one-element array holding $null rather than an empty one - which is a
    # loop body that runs once against nothing.
    $blockDevices = @(Get-Field $parsed 'blockdevices' | Where-Object { $_ })

    $pending = New-Object Collections.Generic.Stack[psobject]
    foreach ($device in $blockDevices) {
        $pending.Push([pscustomobject]@{ Node = $device; Disk = $null })
    }

    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        $node = $item.Node

        $path = [string](Get-Field $node 'path')
        $type = [string](Get-Field $node 'type')
        $size = Get-Field $node 'size'

        # The top-level entry of each tree is the disk; everything below it
        # inherits that path, so a subtree can be selected in one Where-Object.
        $diskPath = if ($item.Disk) { $item.Disk } else { $path }

        $devices.Add([pscustomobject]@{
                Name       = [string](Get-Field $node 'name')
                Path       = $path
                Type       = $type
                # -b was passed, so this is a byte count; older lsblk builds
                # still render it as a string, hence the explicit widening.
                Size       = if ($null -eq $size) { [int64]0 } else { [int64]$size }
                FsType     = [string](Get-Field $node 'fstype')
                Label      = [string](Get-Field $node 'label')
                Uuid       = [string](Get-Field $node 'uuid')
                MountPoint = [string](Get-Field $node 'mountpoint')
                Disk       = $diskPath
            })

        foreach ($child in @(Get-Field $node 'children' | Where-Object { $_ })) {
            $pending.Push([pscustomobject]@{ Node = $child; Disk = $diskPath })
        }
    }

    return $devices
}

function Format-Size {
    param([Parameter(Mandatory)][int64]$Bytes)
    if ($Bytes -le 0) { return '0' }
    return '{0:N1} GiB' -f ($Bytes / 1GB)
}
