<#
.SYNOPSIS
    Reads the pinned third-party tool versions out of scripts/versions.json.

.DESCRIPTION
    Dot-source this from any provisioning script that installs a versioned
    third-party thing:

        . (Join-Path $PSScriptRoot '..\lib\AerieVersions.ps1')
        $K3sVersion = Get-AerieVersion -Name 'k3s.version' -Pattern '^v\d+\.\d+\.\d+\+k3s\d+$'

    Why a file rather than a repository variable: a version pin is structural
    in the sense docs/ethos.md means it - identical for every installation, so
    it belongs in git. Keeping it here also means the pin travels with the
    commit that was tested against it, so a rebuilt node from an old tag gets
    that tag's k3s rather than today's.

    The manifest is read once per session and cached; nothing here mutates it.
    Bumps are commits, not runtime writes.

.NOTES
    Windows PowerShell 5.1 compatible - these run on the Hyper-V hosts, which
    have no pwsh. ConvertFrom-Json there returns PSCustomObject, so every
    lookup checks membership before dereferencing rather than relying on a
    null property read - which keeps this correct under callers that set
    Set-StrictMode -Version Latest and callers that don't.

    Deliberately sets no Set-StrictMode or $ErrorActionPreference at file
    scope, same as hyperv\lib\AerieSsh.ps1: a dot-sourced file's top-level
    statements run in the caller's scope, so doing either here would silently
    change the semantics of whatever script dot-sourced it.
#>

$script:AerieVersionsPath = Join-Path $PSScriptRoot '..\versions.json'
$script:AerieVersionsCache = $null

function Get-AerieVersionManifest {
    <#
    .SYNOPSIS
        Returns the parsed contents of scripts/versions.json.
    #>
    [CmdletBinding()]
    param()

    if ($null -eq $script:AerieVersionsCache) {
        if (-not (Test-Path $script:AerieVersionsPath -PathType Leaf)) {
            throw "Version manifest not found at '$script:AerieVersionsPath'. It is committed to the repository - run this script from a full checkout rather than a copied-out single file."
        }

        try {
            $script:AerieVersionsCache = Get-Content -Path $script:AerieVersionsPath -Raw | ConvertFrom-Json
        }
        catch {
            throw "Version manifest '$script:AerieVersionsPath' is not valid JSON: $($_.Exception.Message)"
        }
    }

    return $script:AerieVersionsCache
}

function Get-AerieVersion {
    <#
    .SYNOPSIS
        Looks up one pinned value by dotted path, e.g. 'k3s.version' or
        'qemuImg.sha256'.

    .PARAMETER Pattern
        Optional regex the value must match. Supply the same pattern the
        calling script puts on its override parameter, so a malformed pin in
        the manifest fails the same way a malformed argument would - at
        preflight, naming the file, instead of somewhere inside a remote
        install script.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Pattern
    )

    $node = Get-AerieVersionManifest
    $walked = @()

    foreach ($segment in ($Name -split '\.')) {
        $walked += $segment
        $reached = $walked -join '.'

        if ($null -eq $node -or $node -isnot [psobject] -or $node.PSObject.Properties.Name -notcontains $segment) {
            throw "Version manifest '$script:AerieVersionsPath' has no entry '$reached' (looking up '$Name'). Add it, or fix the caller."
        }

        $node = $node.$segment
    }

    if ($node -isnot [string] -or [string]::IsNullOrWhiteSpace($node)) {
        throw "Version manifest '$script:AerieVersionsPath': '$Name' must be a non-empty string."
    }

    if ($Pattern -and $node -notmatch $Pattern) {
        throw "Version manifest '$script:AerieVersionsPath': '$Name' is '$node', which doesn't match $Pattern. Pins are exact versions - never a 'latest' or 'stable' channel."
    }

    return $node
}
