<#
.SYNOPSIS
    Generates the ExternalSecret manifests under deploy/ from parameters.json,
    or checks the committed ones still match it.

.DESCRIPTION
    This is the cluster plan Phase 3b.6. docs/secrets-architecture.md promises
    that both halves of the secret story - the seeding script and the manifests
    that read the values back out - agree on one file, and that promise is only
    true if nobody has to remember it. So the manifests are not written by
    hand: they are a rendering of scripts/secrets/parameters.json, and ci.yml
    runs this script with -Check to fail a build where the two have drifted.

    What comes out is one ExternalSecret per (namespace, secretName) pair named
    by the map's 'kubernetes' blocks, with one 'data' entry per parameter that
    names it. A credential pair - an access key id and its secret half - is
    therefore a single object with two keys, not two objects that can
    half-rotate.

    Which parameters get a manifest is entirely the map's business, and it is
    checked here rather than assumed:

      - a parameter with a 'kubernetes' block must be 'required: true'. An
        ExternalSecret pointing at a parameter that no run ever seeded reports
        SecretSyncError forever, which becomes a NotReady Kustomization and
        then a failed phase gate - a whole tree held down by an optional value
        this installation was never going to supply.
      - a 'required: true' parameter must have either a 'kubernetes' block or a
        'kubernetesDeferred' note saying which phase adds one. This is the
        direction that would otherwise rot silently: a secret seeded into the
        parameter store and then never consumed looks, from every side, like a
        secret that works.

    Idempotent: running it twice writes the same bytes, and running it after a
    change to parameters.json is the only way the tree is meant to change.

.PARAMETER MapPath
    parameters.json. The single source for paths, and now for targets.

.PARAMETER OutputPath
    Directory the manifests and their kustomization.yaml are written to. Its
    entire .yaml content is owned by this script - a file in here that the map
    doesn't account for is reported as drift, not left alone.

.PARAMETER ParameterPrefix
    Overrides parameters.json's prefix (default '/aerie'). Must match whatever
    Sync-AerieSecrets.ps1 seeded with: these manifests carry absolute paths,
    because the ClusterSecretStore deliberately sets no 'prefix' of its own.

.PARAMETER StoreName
    The ClusterSecretStore every generated manifest references. Matches
    deploy/cluster/infrastructure/config/cluster-secret-store.yaml, and carries
    no provider in its name for the reason that file explains.

.PARAMETER RefreshInterval
    How often ESO re-reads each parameter, and therefore the worst-case lag
    between rotating a value and the cluster holding it.

.PARAMETER Check
    Render into memory and compare against what's committed, changing nothing.
    Exits non-zero on any difference, naming the files. What ci.yml runs.

.EXAMPLE
    # After adding or retargeting a parameter in parameters.json
    pwsh ./scripts/secrets/New-ExternalSecrets.ps1

.EXAMPLE
    # What CI runs - no writes, non-zero exit on drift
    pwsh ./scripts/secrets/New-ExternalSecrets.ps1 -Check
#>
[CmdletBinding()]
param(
    [string]$MapPath = (Join-Path $PSScriptRoot 'parameters.json'),

    # Forward slashes deliberately: this is the one script in scripts/ that
    # runs on the Linux CI runner as well as on the Windows box, and a
    # backslash-separated relative path is a single literal file name on Linux.
    [string]$OutputPath = (Join-Path $PSScriptRoot '../../deploy/cluster/infrastructure/config/external-secrets'),

    [ValidatePattern('^/[a-z0-9][a-z0-9\-_/]*[a-z0-9]$')]
    [string]$ParameterPrefix,

    [string]$StoreName = 'aerie-secrets',

    [ValidatePattern('^[0-9]+(s|m|h)$')]
    [string]$RefreshInterval = '1h',

    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Write-Utf8Lf {
    <#
    .SYNOPSIS
        Writes text as UTF-8, no BOM, LF line endings.

    .DESCRIPTION
        Both halves matter for -Check to mean anything. The content is compared
        byte for byte against a checkout that may have happened on Windows, so
        the bytes have to be the ones git stores - which is also why
        .gitattributes pins *.yaml to eol=lf. A BOM would additionally reach
        the cluster: it is not whitespace to a YAML parser, and kustomize
        rejects the file rather than ignoring the first three bytes.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function ConvertTo-Lf {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    return $Text.Replace("`r`n", "`n")
}

function Assert-Name {
    <#
    .SYNOPSIS
        Rejects a name Kubernetes would reject, at generation time.

    .DESCRIPTION
        The alternative is a manifest that builds, commits, reconciles, and is
        then refused by the apiserver - surfacing as one NotReady Kustomization
        blocking a whole layer, with the actual complaint several kubectl calls
        away. The map is the right place to be wrong, so this is where the
        wrongness is reported.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][ValidateSet('Namespace', 'SecretName', 'SecretKey')][string]$Kind,
        [Parameter(Mandatory)][string]$Context,
        # Empty is the normal case - it is the list being filled in.
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$Failures
    )

    # DNS-1123 label for a namespace, subdomain for an object name, and the
    # Secret-key charset (which allows dots and underscores, and is the reason
    # these are three checks rather than one).
    $rules = @{
        Namespace  = @{ Pattern = '^[a-z0-9]([-a-z0-9]*[a-z0-9])?$'; MaxLength = 63; Hint = 'lowercase alphanumerics and dashes' }
        SecretName = @{ Pattern = '^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$'; MaxLength = 253; Hint = 'lowercase alphanumerics, dashes and dots' }
        SecretKey  = @{ Pattern = '^[-._a-zA-Z0-9]+$'; MaxLength = 253; Hint = 'alphanumerics, dashes, dots and underscores' }
    }
    $rule = $rules[$Kind]

    if ([string]::IsNullOrWhiteSpace($Value)) {
        $Failures.Add("$Context has no '$Kind'.")
        return
    }
    # -cmatch, not -match: PowerShell's default is case-INsensitive, so an
    # uppercase namespace would pass a pattern written to forbid one.
    if ($Value -cnotmatch $rule.Pattern -or $Value.Length -gt $rule.MaxLength) {
        $Failures.Add("$Context has an invalid $Kind '$Value'. Kubernetes allows $($rule.Hint), starting and ending alphanumeric, up to $($rule.MaxLength) characters.")
    }
}

function New-ManifestHeader {
    param([Parameter(Mandatory)][string]$Summary)
    return @(
        '# GENERATED FILE - do not edit.'
        '#'
        "# $Summary"
        '#'
        '# Source:    scripts/secrets/parameters.json'
        '# Generator: scripts/secrets/New-ExternalSecrets.ps1'
        '# Step:      docs/plans/swarm/phase-3-platform-services.md 3b.6'
        '#'
        '# Regenerate:  pwsh ./scripts/secrets/New-ExternalSecrets.ps1'
        '# Verify:      pwsh ./scripts/secrets/New-ExternalSecrets.ps1 -Check   (ci.yml runs this)'
        '#'
        '# An edit here survives until the next run of either, and then loses. The'
        '# paths, the target namespace, the Secret name and the key inside it are all'
        '# fields of parameters.json - change one there.'
    )
}

function New-ExternalSecretManifest {
    <#
    .SYNOPSIS
        Renders one ExternalSecret: a Secret's worth of parameters.
    #>
    param(
        [Parameter(Mandatory)][string]$Namespace,
        [Parameter(Mandatory)][string]$SecretName,
        [Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][string]$StoreName,
        [Parameter(Mandatory)][string]$RefreshInterval
    )

    $lines = New-Object Collections.Generic.List[string]
    foreach ($line in (New-ManifestHeader -Summary "Secret '$SecretName' in namespace '$Namespace', synced from the $($Entries.Count) parameter(s) below.")) {
        $lines.Add($line)
    }

    $lines.AddRange([string[]]@(
            'apiVersion: external-secrets.io/v1'
            'kind: ExternalSecret'
            'metadata:'
            "  name: $SecretName"
            "  namespace: $Namespace"
            'spec:'
            '  # How long a rotated parameter takes to reach the cluster, worst case.'
            '  # Each tick is one SSM read per data entry, so this is also the cost: the'
            '  # whole tree is a handful of calls an hour.'
            "  refreshInterval: $RefreshInterval"
            '  secretStoreRef:'
            '    # ClusterSecretStore, not SecretStore - one store serves every namespace,'
            '    # and its name carries no provider so swapping Parameter Store for OpenBao'
            '    # stays the one-file change ../cluster-secret-store.yaml describes. That'
            '    # store names the namespace of its own credential explicitly, so nothing'
            '    # here needs a copy of the bootstrap Secret alongside it.'
            '    kind: ClusterSecretStore'
            "    name: $StoreName"
            '  target:'
            "    name: $SecretName"
            '    # Owner: ESO creates the Secret and owns it, so deleting this manifest'
            '    # deletes the Secret with it - which is what makes the git tree the whole'
            '    # truth. Merge would leave orphans behind after a prune.'
            '    creationPolicy: Owner'
            '    # Retain is the default and is set anyway, because the failure it governs'
            '    # is silent and severe: if the parameter disappears upstream, keep the last'
            '    # good value and report SecretSyncError, rather than deleting a live'
            '    # credential out from under a running workload.'
            '    deletionPolicy: Retain'
            '  data:'
        ))

    foreach ($entry in $Entries) {
        if ($entry.Description) { $lines.Add("    # $($entry.Description)") }
        if ($entry.ConsumedBy) { $lines.Add("    # Consumed by: $($entry.ConsumedBy)") }
        $lines.AddRange([string[]]@(
                "    - secretKey: $($entry.SecretKey)"
                '      remoteRef:'
                # Absolute, not relative to a store prefix: parameters.json holds
                # the prefix, and the store deliberately doesn't, so an
                # installation that renames it regenerates this file and changes
                # nothing else.
                "        key: $($entry.Path)"
            ))
    }

    return (($lines -join "`n") + "`n")
}

function New-KustomizationManifest {
    param([Parameter(Mandatory)][string[]]$FileNames)

    $lines = New-Object Collections.Generic.List[string]
    foreach ($line in (New-ManifestHeader -Summary 'Every ExternalSecret the map asks for, one file per target Secret.')) {
        $lines.Add($line)
    }
    $lines.AddRange([string[]]@(
            '#'
            '# Referenced by ../kustomization.yaml, so these land in the infra-config'
            '# layer - which starts only after infra-controllers has converged External'
            '# Secrets and registered the ExternalSecret CRD every file here is an'
            '# instance of. See ../../../infrastructure.yaml.'
            'apiVersion: kustomize.config.k8s.io/v1beta1'
            'kind: Kustomization'
            'resources:'
        ))
    foreach ($name in $FileNames) { $lines.Add("  - $name") }

    return (($lines -join "`n") + "`n")
}

$startedUtc = (Get-Date).ToUniversalTime()

# ---------------------------------------------------------------- #
Write-Stage 'Read and validate the map'
# ---------------------------------------------------------------- #

if (-not (Test-Path $MapPath -PathType Leaf)) {
    throw "Parameter map not found at '$MapPath'."
}
try {
    $map = Get-Content -Path $MapPath -Raw | ConvertFrom-Json
}
catch {
    throw "Parameter map '$MapPath' is not valid JSON: $($_.Exception.Message)"
}

$prefix = if ($ParameterPrefix) { $ParameterPrefix } else { Get-Field $map 'prefix' }
if (-not $prefix) {
    throw "Parameter map '$MapPath' has no 'prefix' and -ParameterPrefix wasn't passed."
}
$prefix = $prefix.TrimEnd('/')

$failures = New-Object Collections.Generic.List[string]
$entries = New-Object Collections.Generic.List[psobject]
$deferred = New-Object Collections.Generic.List[psobject]

foreach ($parameter in (Get-Field $map 'parameters')) {
    $key = [string](Get-Field $parameter 'key')
    $required = [bool](Get-Field $parameter 'required')
    $target = Get-Field $parameter 'kubernetes'
    $deferral = [string](Get-Field $parameter 'kubernetesDeferred')

    if ($target -and $deferral) {
        $failures.Add("$key has both 'kubernetes' and 'kubernetesDeferred'. It is one or the other: either a manifest is generated for it or it is written down why not.")
        continue
    }

    if (-not $target) {
        if ($required -and -not $deferral) {
            $failures.Add("$key is required but has neither a 'kubernetes' block nor a 'kubernetesDeferred' note. A required parameter is seeded by every run of Sync-AerieSecrets.ps1, so if nothing consumes it, that has to be a decision on the record - name the phase that adds the manifest.")
        }
        if ($deferral) {
            $deferred.Add([pscustomobject]@{ Key = $key; Reason = $deferral })
        }
        continue
    }

    # The rule that matters most in this file. An optional parameter is one
    # this installation may legitimately never supply; an ExternalSecret for it
    # is a SecretSyncError that never clears, and infra-config's wait: true
    # turns that into a phase gate nobody can pass.
    if (-not $required) {
        $failures.Add("$key has a 'kubernetes' block but is 'required: false'. An ExternalSecret for a parameter no run seeds sits in SecretSyncError indefinitely and holds its Kustomization NotReady. Flip it to required in the phase that creates the value, and add the manifest then.")
        continue
    }

    $namespace = [string](Get-Field $target 'namespace')
    $secretName = [string](Get-Field $target 'secretName')
    $secretKey = [string](Get-Field $target 'secretKey')

    Assert-Name -Value $namespace -Kind 'Namespace' -Context "$key's kubernetes block" -Failures $failures
    Assert-Name -Value $secretName -Kind 'SecretName' -Context "$key's kubernetes block" -Failures $failures
    Assert-Name -Value $secretKey -Kind 'SecretKey' -Context "$key's kubernetes block" -Failures $failures

    $entries.Add([pscustomobject]@{
            Key         = $key
            Path        = "$prefix/$key"
            Namespace   = $namespace
            SecretName  = $secretName
            SecretKey   = $secretKey
            ConsumedBy  = [string](Get-Field $target 'consumedBy')
            Description = [string](Get-Field $parameter 'description')
        })
}

# Two parameters may share a Secret - that is how a credential pair stays one
# object - but not a key within it, where the second would silently overwrite
# the first at sync time with no complaint from anything.
foreach ($collision in ($entries | Group-Object { "$($_.Namespace)/$($_.SecretName)/$($_.SecretKey)" } | Where-Object { $_.Count -gt 1 })) {
    $keys = ($collision.Group | ForEach-Object { $_.Key }) -join ', '
    $failures.Add("$($collision.Name) is claimed by more than one parameter ($keys). Two parameters can share a Secret, but not a key inside it - one would overwrite the other with no error anywhere.")
}

if ($failures.Count -gt 0) {
    $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
    throw "$MapPath has $($failures.Count) problem(s):`n$detail"
}

Write-Host "Map:       $MapPath"
Write-Host "Prefix:    $prefix"
Write-Host "Store:     $StoreName (refresh $RefreshInterval)"
Write-Host "Targets:   $($entries.Count) parameter(s) with a Kubernetes target, $($deferred.Count) required and deliberately deferred"
foreach ($item in $deferred) {
    Write-Host "  deferred: $($item.Key) - $($item.Reason)"
}

# ---------------------------------------------------------------- #
Write-Stage 'Render'
# ---------------------------------------------------------------- #

# Sorted by namespace then Secret name so the file set is a function of the
# map's content, not of the order its entries happen to appear in; the keys
# *inside* each manifest keep the map's order, which is what puts an access key
# id above its secret half rather than alphabetically below it.
$rendered = [ordered]@{}
$groups = $entries | Group-Object { "$($_.Namespace)/$($_.SecretName)" } | Sort-Object Name
foreach ($group in $groups) {
    $first = $group.Group[0]
    $fileName = "$($first.Namespace)-$($first.SecretName).yaml"
    if ($rendered.Contains($fileName)) {
        throw "Two Secrets render to the same file name '$fileName'. Rename one in parameters.json."
    }
    $rendered[$fileName] = New-ExternalSecretManifest `
        -Namespace $first.Namespace `
        -SecretName $first.SecretName `
        -Entries ([object[]]$group.Group) `
        -StoreName $StoreName `
        -RefreshInterval $RefreshInterval
}

$rendered['kustomization.yaml'] = New-KustomizationManifest -FileNames ([string[]]@($rendered.Keys))

# Flux expands every $VAR in the built output of a path it reconciles, and an
# undefined one becomes an empty string rather than an error. Nothing generated
# here has any business containing a '$', so finding one means a description or
# a consumedBy note picked one up - which would reach the cluster silently
# mangled. Fail here instead, where the fix is to escape it as '$$' in the map.
foreach ($fileName in $rendered.Keys) {
    if ($rendered[$fileName].Contains('$')) {
        throw "'$fileName' contains a '`$'. Flux's postBuild substitution expands it in the built output and an undefined token becomes an empty string, so text from parameters.json that needs a literal dollar sign has to be written '`$`$' there."
    }
}

foreach ($fileName in $rendered.Keys) {
    Write-Host "  $fileName"
}

# ---------------------------------------------------------------- #
Write-Stage $(if ($Check) { 'Check against what is committed' } else { 'Write' })
# ---------------------------------------------------------------- #

$outputFull = [IO.Path]::GetFullPath($OutputPath)
Write-Host "Directory: $outputFull"

$existing = @{}
if (Test-Path $outputFull -PathType Container) {
    foreach ($file in (Get-ChildItem -Path $outputFull -Filter '*.yaml' -File)) {
        $existing[$file.Name] = ConvertTo-Lf (Get-Content -Path $file.FullName -Raw)
    }
}

$drift = New-Object Collections.Generic.List[string]
foreach ($fileName in $rendered.Keys) {
    if (-not $existing.ContainsKey($fileName)) {
        $drift.Add("missing: $fileName")
    }
    elseif ($existing[$fileName] -cne $rendered[$fileName]) {
        $drift.Add("changed: $fileName")
    }
}
# The directory's whole .yaml content is generated, so a file the map no longer
# accounts for is not an unrelated extra - it is an ExternalSecret still syncing
# a credential into the cluster that nothing in parameters.json describes.
foreach ($fileName in $existing.Keys) {
    if (-not $rendered.Contains($fileName)) {
        $drift.Add("stale (no longer in the map): $fileName")
    }
}

if ($Check) {
    if ($drift.Count -gt 0) {
        $detail = ($drift | Sort-Object | ForEach-Object { "  - $_" }) -join "`n"
        throw "The committed ExternalSecrets don't match $MapPath ($($drift.Count) difference(s)):`n$detail`n`nRegenerate and commit: pwsh ./scripts/secrets/New-ExternalSecrets.ps1"
    }
    Write-Host "In sync: $($rendered.Count) file(s) match the map." -ForegroundColor Green
}
else {
    if (-not (Test-Path $outputFull -PathType Container)) {
        New-Item -Path $outputFull -ItemType Directory -Force | Out-Null
    }
    foreach ($fileName in $rendered.Keys) {
        Write-Utf8Lf -Path (Join-Path $outputFull $fileName) -Content $rendered[$fileName]
    }
    foreach ($fileName in $existing.Keys) {
        if (-not $rendered.Contains($fileName)) {
            Remove-Item -Path (Join-Path $outputFull $fileName) -Force
        }
    }
    if ($drift.Count -eq 0) {
        Write-Host "Already up to date: $($rendered.Count) file(s) unchanged." -ForegroundColor Green
    }
    else {
        Write-Host "Wrote $($rendered.Count) file(s):" -ForegroundColor Green
        foreach ($item in ($drift | Sort-Object)) { Write-Host "  $item" }
    }
}

# ---------------------------------------------------------------- #
Write-Stage 'Done'
# ---------------------------------------------------------------- #

$elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalSeconds, 1)
Write-Host "Finished in ${elapsed}s."
if (-not $Check) {
    Write-Host ''
    Write-Host 'Nothing has reached a cluster: these are files. Flux applies them when they are'
    Write-Host 'committed, and each one is Ready when ESO has read the parameter behind it -'
    Write-Host '`kubectl -n <namespace> get externalsecret` is the check.'
}
