<#
.SYNOPSIS
    Renders this installation's operator values into the `aerie-cluster-config`
    ConfigMap and applies it to the cluster over SSH - the substitution source
    every Kustomization under deploy/ reads through postBuild.substituteFrom.

.DESCRIPTION
    the cluster plan Phase 3b's first step, and the one that has to run before the
    first commit under deploy/. Flux reconciles from git, where docs/ethos.md
    forbids operator values; Phases 0-2 passed them as vars.* at deploy time,
    which a Flux-reconciled manifest has no equivalent of. This is the bridge:
    the values arrive out of band as one ConfigMap in flux-system, and the
    manifests reference them as ${DOMAIN}, ${INGRESS_VIP} and so on.

    Same shape as Provision 2's bootstrap Secret, minus the secrecy - and the
    absence of secrecy is a feature, not an omission. Every value here is an
    operator value rather than credential material, so the manifest is applied
    in the clear and printed in full in the run log, which is what makes the
    job summary an audit record of what the cluster was actually configured
    with.

    cluster-config.json is the map: which keys exist, which environment
    variable supplies each, and what shape a valid value has. It holds no
    values and is identical for every installation, which is why it is
    committed. Change a key there and nowhere else - the ${...} tokens in
    deploy/ read the same file.

    Stages:
      1. Preflight - the map parses, the SSH key resolves, the OpenSSH client
                     is present, the node answers 22, and every required value
                     is both set and syntactically valid. Rejecting a value
                     here is the whole point of the pattern field: a typo'd
                     hosted zone id is otherwise diagnosed at 3b.10 as what
                     looks like an AWS permissions failure.
      2. Inspect   - the apiserver answers, NODE_INTERFACE actually exists on
                     the node and INGRESS_VIP sits on its subnet, and the
                     ConfigMap already present (if any) is diffed against what
                     this run would write.
      3. Apply     - one `kubectl apply` of the rendered ConfigMap, over the
                     SSH channel's standard input.
      4. Verify    - reads the ConfigMap back and asserts every key arrived
                     with the value that was sent.

    Idempotent and re-runnable: this is how a value gets *changed* later. Set
    the repository variable, re-dispatch, and the next reconciliation picks it
    up - no commit, which is the entire reason these live in a ConfigMap
    rather than in the manifests. Because every run applies the full key set,
    a key deleted from cluster-config.json is also pruned from the cluster by
    apply, so the map stays the whole truth rather than an append-only log.

.PARAMETER IPAddress
    A k3s server's LAN address - the kubectl target. Node 1 today; see
    docs/secrets-architecture.md on why that is a known, accepted single point
    of failure until Phase 7. Re-running this against a surviving server is
    the recovery, and it is safe to do at any time.

.PARAMETER PreflightOnly
    Runs stages 1 and 2 and stops without applying anything. Since stage 2
    diffs the live ConfigMap, this prints exactly what a real run would change
    - the useful thing to dispatch before changing a value on a cluster that
    is already serving.

.EXAMPLE
    $env:DOMAIN = 'example.com'; $env:ACME_EMAIL = 'admin@example.com'
    # ... and the other five, per cluster-config.json
    .\Set-ClusterConfig.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$MapPath = (Join-Path $PSScriptRoot 'cluster-config.json'),

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

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

function Get-EnvValue {
    <#
    .SYNOPSIS
        Reads an environment variable, treating whitespace-only as unset.

    .DESCRIPTION
        An unset GitHub Actions variable arrives as an empty string rather
        than an absent variable, so "is it set" has to mean "does it have
        content" - otherwise an unconfigured value is applied as ''. That
        would be worse here than an outright failure: a Kustomization
        substitutes the empty string happily, and the first symptom is a
        certificate for the domain `.` or a Service asking for the address ''.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $value = (Get-Item "env:$Name" -ErrorAction SilentlyContinue)
    if ($null -eq $value) { return $null }
    if ([string]::IsNullOrWhiteSpace($value.Value)) { return $null }
    return $value.Value.Trim()
}

function ConvertTo-UInt32Address {
    param([Parameter(Mandatory)][string]$Address)
    $bytes = ([ipaddress]$Address).GetAddressBytes()
    [array]::Reverse($bytes)
    return [int64][BitConverter]::ToUInt32($bytes, 0)
}

function Test-IpInSubnet {
    <#
    .SYNOPSIS
        True when -Address falls inside the network -Cidr describes.

    .DESCRIPTION
        Deliberately arithmetic rather than a call into System.Net: this runs
        on Windows PowerShell 5.1, which has no IPNetwork type, and the
        alternative is string-comparing octets. Everything is widened to
        [int64] because PowerShell's shift operators produce a signed result
        and a /0 through /8 mask overflows [int32].
    #>
    param(
        [Parameter(Mandatory)][string]$Address,
        [Parameter(Mandatory)][string]$NetworkAddress,
        [Parameter(Mandatory)][int]$PrefixLength
    )
    if ($PrefixLength -lt 0 -or $PrefixLength -gt 32) { return $false }
    $mask = if ($PrefixLength -eq 0) { [int64]0 } else {
        ([int64][uint32]::MaxValue -shl (32 - $PrefixLength)) -band [int64][uint32]::MaxValue
    }
    return ((ConvertTo-UInt32Address $Address) -band $mask) -eq ((ConvertTo-UInt32Address $NetworkAddress) -band $mask)
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$results = New-Object Collections.Generic.List[psobject]

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    if (-not (Test-Path $MapPath -PathType Leaf)) {
        throw "Cluster config map not found at '$MapPath'."
    }
    try {
        $map = Get-Content -Path $MapPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Cluster config map '$MapPath' is not valid JSON: $($_.Exception.Message)"
    }

    $namespace = Get-Field $map 'namespace'
    $configMapName = Get-Field $map 'configMapName'
    $keys = @(Get-Field $map 'keys')

    # These two are interpolated into a remote shell command below. They come
    # from a committed file rather than from an operator, so this is defence
    # in depth rather than a live risk - but a file that is edited is a file
    # that can be edited wrongly, and the check costs one line. Kubernetes
    # object names are RFC 1123 labels anyway, so this rejects nothing valid.
    $nameRules = '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'
    if (-not $namespace -or $namespace -cnotmatch $nameRules) {
        throw "Cluster config map '$MapPath' has a missing or invalid 'namespace'. It must be an RFC 1123 label, and it must be the namespace the Flux Kustomizations live in - postBuild.substituteFrom resolves a ConfigMap in the Kustomization's own namespace, not cluster-wide."
    }
    if (-not $configMapName -or $configMapName -cnotmatch $nameRules) {
        throw "Cluster config map '$MapPath' has a missing or invalid 'configMapName'."
    }
    if ($keys.Count -eq 0) {
        throw "Cluster config map '$MapPath' declares no keys, so there is nothing to apply."
    }

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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-cluster-config'
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

    # Every value is collected and checked before anything is applied, so a
    # run either writes the whole ConfigMap or writes none of it. A partial
    # substitution source is worse than an absent one: a Kustomization missing
    # a variable fails loudly, while one that substitutes half its values
    # reconciles a manifest nobody wrote.
    $values = [ordered]@{}
    foreach ($entry in $keys) {
        $key = Get-Field $entry 'key'
        $envName = Get-Field $entry 'env'
        $required = Get-Field $entry 'required'
        $pattern = Get-Field $entry 'pattern'
        $hint = Get-Field $entry 'hint'
        $example = Get-Field $entry 'example'
        $source = Get-Field $entry 'githubSource'
        $normalize = Get-Field $entry 'normalize'

        if (-not $key -or -not $envName) {
            throw "Cluster config map '$MapPath' has an entry missing 'key' or 'env'."
        }

        $value = Get-EnvValue $envName
        if ($value -and $normalize -eq 'lower') { $value = $value.ToLowerInvariant() }
        if (-not $value) {
            if ($required) {
                $failures.Add("$key is not set. provision-4-cluster-config.yml maps it from the '$source' repository variable - Settings > Secrets and variables > Actions, on the Variables tab. If it looks set already, check it isn't on the Secrets tab: this reads it as a variable, and the wrong tab resolves to an empty string rather than an error. Example value: $example")
            }
            continue
        }

        # -cnotmatch, not -notmatch: PowerShell's comparison operators are
        # case-*insensitive* by default, so `^[A-Z0-9]+$` accepts lowercase
        # and `^[a-z]` accepts uppercase - the pattern reads as if it
        # constrains case and silently does not. That is not academic here:
        # a Route53 hosted zone id is an opaque, case-sensitive string, so a
        # lowercased one is simply a zone that does not exist, and AWS reports
        # it as NoSuchHostedZone from inside cert-manager. Where case genuinely
        # carries no meaning - a domain name, by RFC - the entry declares
        # `normalize` above and is folded rather than rejected.
        if ($pattern -and $value -cnotmatch $pattern) {
            # The value is echoed back deliberately - it is an operator value,
            # not a secret, and "what you actually typed" is most of
            # diagnosing a transcription error.
            $failures.Add("$key = '$value' doesn't look valid.$(if ($hint) { " $hint" }) Expected something of the shape '$example'.")
            continue
        }

        $values[$key] = $value
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Cluster:   $IPAddress"
    Write-Host "ConfigMap: $configMapName in $namespace"
    Write-Host "Map:       $MapPath ($($values.Count) key(s))"
    if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    Write-Host 'Preflight OK.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-cluster-config-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Inspect'
    # ---------------------------------------------------------------- #

    $probe = Invoke-NodeSsh @ssh -Command 'sudo k3s kubectl get --raw /readyz' -ConnectTimeoutSec 20
    if ($probe.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
        throw "Couldn't reach the apiserver on $IPAddress as '$Username'$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdOut)$($probe.StdErr)"
    }
    Write-Host "apiserver: $($probe.StdOut.Trim())"

    # NODE_INTERFACE and INGRESS_VIP are the two values whose correctness the
    # node itself can settle, and they are also the two whose failure mode is
    # worst: kube-vip installs cleanly against a wrong interface and simply
    # never answers ARP, which presents as a networking problem several steps
    # after the transcription error that caused it. The interface name matched
    # the pattern above, so it holds nothing a shell would treat specially.
    if ($values.Contains('NODE_INTERFACE')) {
        $iface = $values['NODE_INTERFACE']
        $addr = Invoke-NodeSsh @ssh -Command "ip -o -4 addr show dev $iface" -ConnectTimeoutSec 20
        if ($addr.ExitCode -ne 0) {
            # Trimmed here rather than with a remote `tr -d [:blank:]`: an
            # unquoted bracket expression is a glob to the remote shell, and
            # quoting it would need the double quotes Invoke-NodeSsh refuses.
            $available = Invoke-NodeSsh @ssh -Command 'ip -o link show | cut -d: -f2' -ConnectTimeoutSec 20
            $names = (($available.StdOut -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ', '
            throw "NODE_INTERFACE '$iface' doesn't exist on $IPAddress. kube-vip advertises the VIP on this interface by name in ARP mode, so a wrong name is a VIP that is configured and never answers. Interfaces on this node: $names"
        }

        $cidrs = [regex]::Matches($addr.StdOut, 'inet\s+(\d+\.\d+\.\d+\.\d+)/(\d+)')
        if ($cidrs.Count -eq 0) {
            Write-Warning "NODE_INTERFACE '$iface' exists on $IPAddress but carries no IPv4 address, so INGRESS_VIP can't be checked against its subnet. That is worth understanding before 3b.8 - kube-vip's ARP mode needs the VIP to be on the same layer 2 as this interface."
        }
        elseif ($values.Contains('INGRESS_VIP')) {
            $vip = $values['INGRESS_VIP']
            $onSubnet = $false
            $described = New-Object Collections.Generic.List[string]
            foreach ($match in $cidrs) {
                $nodeAddress = $match.Groups[1].Value
                $prefix = [int]$match.Groups[2].Value
                $described.Add("$nodeAddress/$prefix")
                if ($nodeAddress -eq $vip) {
                    if ($prefix -eq 32) {
                        # kube-vip's own ARP-mode advertisement, not a real
                        # address of this node - see
                        # deploy/cluster/infrastructure/controllers/kube-vip.yaml's
                        # GlobalLeader footnote. Once a node is elected, it adds
                        # the VIP to its own interface as a /32 host route,
                        # which is indistinguishable from a real conflict by
                        # address alone. Confirmed live, 2026-08-19: the leader
                        # reports its real address at the LAN's actual prefix
                        # and the VIP separately at /32, `scope global
                        # deprecated`. A genuine operator error - INGRESS_VIP
                        # typo'd to the node's own static or DHCP-leased
                        # address - carries that real prefix instead, which the
                        # branch below still catches. Without this carve-out,
                        # Provision 4 could never be re-dispatched against a
                        # cluster kube-vip is already serving from, which is
                        # exactly what a post-3b.8 run - 6b.3's - needs to do.
                        $onSubnet = $true
                        continue
                    }
                    throw "INGRESS_VIP $vip is already $iface's own address on $IPAddress, as a /$prefix - not kube-vip's /32 VIP advertisement. The VIP is a second, floating address that moves between nodes - it can never be a node's real one, and giving kube-vip a node address to advertise takes that node off the network when the VIP moves."
                }
                if (Test-IpInSubnet -Address $vip -NetworkAddress $nodeAddress -PrefixLength $prefix) { $onSubnet = $true }
            }
            if (-not $onSubnet) {
                throw "INGRESS_VIP $vip is not on any subnet $iface is attached to on $IPAddress ($($described -join ', ')). kube-vip is configured in ARP mode (3b.8), which answers for the VIP on this interface's own layer 2 - an address outside it is advertised to a segment no client is on, and nothing routes to it. Pick an address on the node subnet, outside the DHCP pool, per Phase 3a.2."
            }
            Write-Host "$iface on ${IPAddress}: $($described -join ', ') - INGRESS_VIP $vip is on its subnet."
        }
    }

    # `|| echo {}` rather than letting a non-zero exit fail the run: a
    # first-ever run has no ConfigMap and no namespace, and "not found" is the
    # expected answer then rather than an error.
    $existingRaw = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl -n $namespace get configmap $configMapName -o json 2>/dev/null || echo {}" -ConnectTimeoutSec 20
    if ($existingRaw.ExitCode -ne 0) {
        throw "Couldn't read the existing ConfigMap on $IPAddress (exit $($existingRaw.ExitCode)):`n$($existingRaw.StdErr)"
    }
    $existingData = $null
    try {
        $existing = $existingRaw.StdOut | ConvertFrom-Json
        $existingData = Get-Field $existing 'data'
    }
    catch {
        throw "The cluster returned something that isn't JSON when asked for ${configMapName}:`n$($existingRaw.StdOut)"
    }

    foreach ($key in $values.Keys) {
        $current = if ($existingData) { Get-Field $existingData $key } else { $null }
        $status = if ($null -eq $current) { 'create' } elseif ($current -eq $values[$key]) { 'unchanged' } else { "update (was '$current')" }
        $results.Add([pscustomobject]@{ Key = $key; Value = $values[$key]; Status = $status })
    }

    # A key the cluster has and the map no longer declares. Apply prunes it,
    # because every run sends the complete set - worth saying out loud, since
    # a substitution that silently stops resolving is a manifest that fails to
    # reconcile with a message about an unset variable rather than about this.
    if ($existingData) {
        foreach ($property in $existingData.PSObject.Properties) {
            if (-not $values.Contains($property.Name)) {
                $results.Add([pscustomobject]@{ Key = $property.Name; Value = $property.Value; Status = 'prune (no longer in cluster-config.json)' })
            }
        }
    }

    Write-Host ''
    $results | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here. The table above is exactly what a real run would change; nothing was applied.' -ForegroundColor Yellow
        return
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Apply'
    # ---------------------------------------------------------------- #

    # The Namespace is included for the same reason Provision 2 includes
    # external-secrets: applying one that exists is a no-op, and a rebuilt
    # cluster shouldn't depend on whether Flux or this ran first.
    # Quoted, always. An unquoted YAML scalar is typed, so
    # `LONGHORN_REPLICA_COUNT: 2` is an integer and the apiserver rejects the
    # whole ConfigMap - every value in `data` has to be a string. Single
    # quotes have exactly one escape and this is it; no current pattern admits
    # an apostrophe, so the escape is here for the key added later by someone
    # who never reads this line.
    $dataLines = foreach ($key in $values.Keys) {
        $escaped = $values[$key] -replace "'", "''"
        "  ${key}: '$escaped'"
    }

    $manifest = @(
        'apiVersion: v1'
        'kind: Namespace'
        'metadata:'
        "  name: $namespace"
        '---'
        'apiVersion: v1'
        'kind: ConfigMap'
        'metadata:'
        "  name: $configMapName"
        "  namespace: $namespace"
        '  labels:'
        '    app.kubernetes.io/managed-by: aerie-provision-4'
        '  annotations:'
        # Structural breadcrumb for whoever finds this ConfigMap and wonders
        # why values that look like configuration are not in the repository.
        '    aerie.internal/why: "Operator values for this installation, substituted into every Kustomization under deploy/ via postBuild.substituteFrom. Deliberately not committed: docs/ethos.md keeps per-installation values out of git. Managed by scripts/k3s/Set-ClusterConfig.ps1, never by Flux - change a value by re-running Provision 4, not by editing this object."'
        'data:'
    ) + $dataLines

    # Trailing newline on purpose: Windows PowerShell terminates a piped
    # string with CRLF, so without it the last data line would carry a stray
    # carriage return into the YAML. The BOM this channel may prepend is
    # harmless here for the same reason it is in Provision 2 - a byte order
    # mark is legal at the start of a YAML stream - which is a property of the
    # format rather than of the pipe; see Invoke-NodeSsh.
    $apply = Invoke-NodeSsh @ssh -Command 'sudo k3s kubectl apply -f -' -StdIn (($manifest -join "`n") + "`n") -ConnectTimeoutSec 30
    if ($apply.ExitCode -ne 0) {
        throw "Applying $configMapName to $IPAddress failed (exit $($apply.ExitCode)):`n$($apply.StdOut)$($apply.StdErr)"
    }
    Write-Host $apply.StdOut.TrimEnd()

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    $readBack = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl -n $namespace get configmap $configMapName -o json" -ConnectTimeoutSec 20
    if ($readBack.ExitCode -ne 0) {
        throw "$configMapName applied but couldn't be read back (exit $($readBack.ExitCode)):`n$($readBack.StdErr)"
    }
    $appliedData = Get-Field ($readBack.StdOut | ConvertFrom-Json) 'data'
    if (-not $appliedData) {
        throw "$configMapName exists on $IPAddress but has no data. Nothing would substitute."
    }

    $mismatches = New-Object Collections.Generic.List[string]
    foreach ($key in $values.Keys) {
        $stored = Get-Field $appliedData $key
        if ($null -eq $stored) { $mismatches.Add("$key is missing"); continue }
        if ($stored -ne $values[$key]) { $mismatches.Add("$key is '$stored', expected '$($values[$key])'") }
    }
    if ($mismatches.Count -gt 0) {
        throw "$configMapName read back wrong:`n$(($mismatches | ForEach-Object { "  - $_" }) -join "`n")"
    }

    $storedKeys = @($appliedData.PSObject.Properties.Name | Sort-Object)
    Write-Host "$configMapName in ${namespace}: $($storedKeys.Count) key(s) - $($storedKeys -join ', ')"

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "Cluster configuration applied in ${elapsed} min." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Every Kustomization under deploy/ can now reference these as ${NAME} and reach them'
    Write-Host "through postBuild.substituteFrom the $configMapName ConfigMap. A Kustomization whose"
    Write-Host 'substitution source is missing fails to reconcile rather than degrading gracefully,'
    Write-Host 'which is why this step comes before the first commit under deploy/.'
    Write-Host ''
    Write-Host 'If a value changed and something is already reconciling it, the next pass picks it up'
    Write-Host 'on its own interval. To not wait:'
    Write-Host "  ssh $Username@$IPAddress sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux reconcile kustomization flux-system --with-source"
    Write-Host ''
    Write-Warning "This targeted $IPAddress directly - there is no VIP in front of the apiserver (kube-vip fronts ingress only, and does not exist yet). If that node is gone, re-run this against a surviving server; it's idempotent. Known and accepted until Phase 7; see docs/secrets-architecture.md."

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $lines = @(
            "## Cluster configuration applied to $tick$IPAddress$tick"
            ''
            "$tick$configMapName$tick in $tick$namespace$tick, from $tick$(Split-Path $MapPath -Leaf)$tick."
            ''
            '| Key | Value | Status |'
            '|---|---|---|'
        )
        foreach ($result in $results) {
            $lines += "| $tick$($result.Key)$tick | $tick$($result.Value)$tick | $($result.Status) |"
        }
        $lines += @(
            ''
            '_Operator values, not secrets - printed in full on purpose, so this run is the record of'
            'what the cluster was configured with. Anything sensitive goes through Provision 2 instead._'
        )
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
}
