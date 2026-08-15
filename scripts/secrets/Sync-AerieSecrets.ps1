<#
.SYNOPSIS
    Seeds this installation's secrets into the AWS SSM Parameter Store tree
    described by parameters.json, then creates the single bootstrap Secret the
    cluster needs to read that tree back - the one imperative secret injection
    Aerie's ethos allows.

.DESCRIPTION
    This is TODO_SWARM.md Phase 2's "Provision 2: Seed secrets" step, and the
    same shape as Install-K3sNode.ps1 one step before it: every machine-
    specific fact is a parameter, the SSH plumbing is reused from
    ..\hyperv\lib\AerieSsh.ps1, and the GitHub Actions workflow that wraps it
    (provision-2-seed-secrets.yml) is the primary way it runs.

    Secret values are never passed as script parameters. They arrive as
    environment variables named by parameters.json, so they stay out of the
    command line, out of PowerShell's history, and out of this file.

    Stages:
      1. Preflight - the map parses, the AWS CLI is present, every required
                     value is set, the seed-writer and ESO credentials are
                     set, and (unless -SkipBootstrap) the node answers SSH.
      2. Seed      - for each mapped parameter: read the current value, skip
                     it if unchanged, otherwise put it as a SecureString.
                     Skipping is what keeps a re-run from minting a new
                     parameter version per secret per run.
      3. Verify    - re-reads the tree using the *ESO* credential rather than
                     the writer's, which proves the bootstrap credential works
                     and its IAM policy is right before ESO exists to find out
                     the hard way.
      4. Bootstrap - applies the ESO bootstrap Secret to the cluster over SSH.

    Idempotent and re-runnable end to end: run it again after rotating one
    value and only that parameter changes.

.PARAMETER IPAddress
    A k3s server's LAN address - the kubectl target for the bootstrap Secret.
    Node 1 today; see docs/secrets-architecture.md on why that's a known,
    accepted single point of failure until Phase 7.

.PARAMETER ParameterPrefix
    Overrides parameters.json's prefix (default '/aerie'). Two installations
    sharing one AWS account give each its own prefix; the ESO IAM policy and
    the Phase 3 ClusterSecretStore have to agree with whatever is chosen.

.PARAMETER SkipBootstrap
    Seed the parameter store only. What to use before any node exists.

.PARAMETER SkipSeed
    Apply the bootstrap Secret only - e.g. rebuilding a cluster whose
    parameter tree is already correct.

.EXAMPLE
    # Both halves, from a host that can reach node 1
    .\Sync-AerieSecrets.ps1 -IPAddress 10.0.0.21 -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    # Parameter store only, before the cluster exists
    .\Sync-AerieSecrets.ps1 -SkipBootstrap
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [string]$MapPath = (Join-Path $PSScriptRoot 'parameters.json'),

    [ValidatePattern('^/[a-z0-9][a-z0-9\-_/]*[a-z0-9]$')]
    [string]$ParameterPrefix,

    [string]$AwsRegion,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [switch]$SkipSeed,
    [switch]$SkipBootstrap,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')
. (Join-Path $PSScriptRoot '..\runner\lib\AerieRunnerDependencies.ps1')

$script:StageNumber = 0
# Replaced in preflight with the resolved absolute path, so every later call
# runs the same binary the preflight check found.
$script:AwsCommand = 'aws'

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
        An unset GitHub Actions secret arrives as an empty string rather than
        an absent variable, so "is it set" has to mean "does it have content"
        or every unconfigured optional secret would be seeded as ''.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $value = (Get-Item "env:$Name" -ErrorAction SilentlyContinue)
    if ($null -eq $value) { return $null }
    if ([string]::IsNullOrWhiteSpace($value.Value)) { return $null }
    return $value.Value
}

function Invoke-Aws {
    <#
    .SYNOPSIS
        Runs one AWS CLI command and returns its exit code and output without
        throwing, in the shape Invoke-NodeSsh uses.

    .DESCRIPTION
        Non-zero is an ordinary result here - ParameterNotFound is how a
        first-ever seed reports "create this one" - so the caller decides what
        counts as failure. $ErrorActionPreference drops to Continue for the
        same Windows PowerShell 5.1 reason Invoke-NodeSsh documents: under
        Stop, redirecting a native command's stderr raises a terminating
        NativeCommandError.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments)

    $stdout = [IO.Path]::GetTempFileName()
    $stderr = [IO.Path]::GetTempFileName()
    try {
        $ErrorActionPreference = 'Continue'
        & $script:AwsCommand @Arguments 1> $stdout 2> $stderr
        $exitCode = $LASTEXITCODE

        [pscustomobject]@{
            ExitCode = $exitCode
            StdOut   = [string](Get-Content -Path $stdout -Raw -ErrorAction SilentlyContinue)
            StdErr   = [string](Get-Content -Path $stderr -Raw -ErrorAction SilentlyContinue)
        }
    }
    finally {
        Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Write-Utf8NoBom {
    <#
    .SYNOPSIS
        Writes text as UTF-8 without a byte-order mark.

    .DESCRIPTION
        Windows PowerShell 5.1's `Out-File -Encoding utf8` emits a BOM, which
        the AWS CLI's --cli-input-json rejects as invalid JSON. This is the
        only reliable way to hand it a file it can parse.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

# Every AWS variable this script writes, captured before it writes any of
# them so the caller's environment survives the run. Blanket-removing them in
# the finally block would silently break a second call in the same session -
# which is exactly how -SkipBootstrap then -SkipSeed gets run by hand.
$script:AwsEnvNames = @('AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_REGION', 'AWS_DEFAULT_REGION', 'AWS_SESSION_TOKEN', 'AWS_PROFILE')
$script:AwsEnvSnapshot = @{}
foreach ($name in $script:AwsEnvNames) {
    $existing = Get-Item "env:$name" -ErrorAction SilentlyContinue
    $script:AwsEnvSnapshot[$name] = if ($existing) { $existing.Value } else { $null }
}

function Restore-AwsEnvironment {
    foreach ($name in $script:AwsEnvNames) {
        $original = $script:AwsEnvSnapshot[$name]
        if ($null -eq $original) {
            Remove-Item "env:$name" -ErrorAction SilentlyContinue
        }
        else {
            Set-Item "env:$name" -Value $original
        }
    }
}

function Use-AwsIdentity {
    <#
    .SYNOPSIS
        Points the AWS CLI at one specific credential pair for subsequent
        calls in this process.

    .DESCRIPTION
        Two different IAM users are used in one run - the seed writer and the
        read-only ESO user - and the runner may also carry ambient AWS
        credentials for unrelated jobs. Setting them explicitly per stage is
        what stops a seed from silently running as whatever identity happened
        to be in the environment. Cleared in the script's finally block.
    #>
    param(
        [Parameter(Mandatory)][string]$AccessKeyId,
        [Parameter(Mandatory)][string]$SecretAccessKey,
        [Parameter(Mandatory)][string]$Region
    )
    $env:AWS_ACCESS_KEY_ID = $AccessKeyId
    $env:AWS_SECRET_ACCESS_KEY = $SecretAccessKey
    # Both spellings: AWS_REGION wins over AWS_DEFAULT_REGION in the CLI, so
    # setting only the latter would let an inherited AWS_REGION quietly send
    # these writes to a different region than -AwsRegion asked for.
    $env:AWS_REGION = $Region
    $env:AWS_DEFAULT_REGION = $Region
    # An inherited session token belongs to whatever identity set it, and
    # would be rejected alongside these long-lived keys.
    Remove-Item env:AWS_SESSION_TOKEN -ErrorAction SilentlyContinue
    Remove-Item env:AWS_PROFILE -ErrorAction SilentlyContinue
}

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()
$results = New-Object Collections.Generic.List[psobject]
$bootstrapStatus = 'skipped'

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    if ($SkipSeed -and $SkipBootstrap) {
        throw '-SkipSeed and -SkipBootstrap together leave nothing to do.'
    }

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

    $region = if ($AwsRegion) { $AwsRegion } else { Get-EnvValue 'AWS_REGION' }
    if (-not $region) {
        $failures.Add("No AWS region: pass -AwsRegion or set AWS_REGION. The parameter tree lives in exactly one region and both this script and the Phase 3 ClusterSecretStore have to name the same one.")
    }

    # Resolved through the runner-dependency library rather than Get-Command
    # alone: that also looks in the MSI's install directory, which is how a
    # CLI installed earlier in this same job is found. The machine PATH entry
    # the installer writes is invisible to the runner service - and every
    # process it spawns - until the service restarts. Deliberately resolve
    # only, never install: preflight runs under whatever account launched this
    # script, and a check that mutates the machine it's checking isn't a check.
    $awsPath = Get-AerieAwsCliPath
    if ($awsPath) {
        $script:AwsCommand = $awsPath
    }
    else {
        $failures.Add("The AWS CLI isn't installed. The 'Ensure runner dependencies' step in provision-2-seed-secrets.yml installs it - if this ran from that workflow, read that step's log. By hand: scripts\runner\Install-RunnerDependencies.ps1 -Dependency AwsCli from an elevated PowerShell.")
    }

    # The seed writer and the ESO reader are deliberately different IAM users:
    # the credential the cluster holds forever must not be able to write the
    # tree it reads. The writer reads as well as writes - read-before-write in
    # Seed needs it - so the split is one-directional, not disjoint. Both are
    # needed even for a -SkipSeed run, since Verify exercises the ESO half and
    # Bootstrap plants it.
    $writerKeyId = Get-EnvValue 'SSM_AWS_ACCESS_KEY_ID'
    $writerSecret = Get-EnvValue 'SSM_AWS_SECRET_ACCESS_KEY'
    if (-not $SkipSeed -and (-not $writerKeyId -or -not $writerSecret)) {
        $failures.Add("SSM_AWS_ACCESS_KEY_ID (repository variable) / SSM_AWS_SECRET_ACCESS_KEY (repository secret) aren't both set. These are the seed writer's credentials (ssm:PutParameter + ssm:GetParameter on $prefix/*), separate from the read-only ESO user - see docs/secrets-architecture.md.")
    }

    $bootstrap = Get-Field $map 'bootstrap'
    if (-not $bootstrap) { throw "Parameter map '$MapPath' has no 'bootstrap' section." }
    $bootstrapKeys = Get-Field $bootstrap 'keys'
    $bootstrapValues = [ordered]@{}
    foreach ($keyName in $bootstrapKeys.PSObject.Properties.Name) {
        $envName = Get-Field $bootstrapKeys.$keyName 'env'
        $value = Get-EnvValue $envName
        if (-not $value) {
            $kind = [string](Get-Field $bootstrapKeys.$keyName 'githubKind')
            if (-not $kind) { $kind = 'secret' }
            $failures.Add("$envName isn't set. It supplies the '$keyName' key of the ESO bootstrap Secret - the aerie-eso IAM user's credential. It's the '$(Get-Field $bootstrapKeys.$keyName 'githubSource')' repository $kind.")
        }
        $bootstrapValues[$keyName] = $value
    }

    # Resolve every mapped value up front so a missing required secret fails
    # before anything has been written, rather than half way through the tree.
    $planned = New-Object Collections.Generic.List[psobject]
    foreach ($parameter in (Get-Field $map 'parameters')) {
        $key = Get-Field $parameter 'key'
        $envName = Get-Field $parameter 'env'
        $required = [bool](Get-Field $parameter 'required')
        $value = Get-EnvValue $envName

        if (-not $value -and $required) {
            $kind = [string](Get-Field $parameter 'githubKind')
            if (-not $kind) { $kind = 'secret' }
            $failures.Add("$envName isn't set, and $prefix/$key is required. Add it as the '$(Get-Field $parameter 'githubSource')' repository $kind - Settings > Secrets and variables > Actions, $(if ($kind -eq 'variable') { 'Variables' } else { 'Secrets' }) tab - or mark the parameter optional in parameters.json if this installation genuinely doesn't have one.")
        }

        $planned.Add([pscustomobject]@{
                Path        = "$prefix/$key"
                EnvName     = $envName
                Value       = $value
                Required    = $required
                Description = [string](Get-Field $parameter 'description')
            })
    }

    if (-not $SkipBootstrap) {
        if (-not $IPAddress) {
            $failures.Add('-IPAddress is required unless -SkipBootstrap is passed: the bootstrap Secret is applied to a k3s server over SSH.')
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
                $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-secrets'
                $privateKeyPath = $resolvedKey.Path
                $tempKeyFile = $resolvedKey.TempFile
                $keyFingerprint = $resolvedKey.Fingerprint
            }
            catch {
                $failures.Add($_.Exception.Message)
            }
        }

        if ($IPAddress -and -not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
            $failures.Add("$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right.")
        }
        if ($IPAddress -and -not (Test-TcpPort -IPAddress $IPAddress -Port 6443)) {
            $failures.Add("$IPAddress isn't answering on port 6443. Run Provision 1 against this node first - there's no apiserver to apply the bootstrap Secret to.")
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    $withValue = @($planned | Where-Object { $_.Value })
    Write-Host "Map:       $MapPath"
    Write-Host "Prefix:    $prefix (region $region)"
    Write-Host "Seeding:   $($withValue.Count) of $($planned.Count) mapped parameters have a value this run"
    if (-not $SkipBootstrap) {
        Write-Host "Cluster:   $IPAddress -> secret/$(Get-Field $bootstrap 'secretName') in namespace $(Get-Field $bootstrap 'namespace')"
        if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    }
    Write-Host 'Preflight OK.'

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here without writing anything.' -ForegroundColor Yellow
        return
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Seed parameter store'
    # ---------------------------------------------------------------- #

    if ($SkipSeed) {
        Write-Host '-SkipSeed: leaving the parameter tree as it is.'
        foreach ($item in $planned) {
            $results.Add([pscustomobject]@{ Path = $item.Path; Status = 'skipped (-SkipSeed)' })
        }
    }
    else {
        Use-AwsIdentity -AccessKeyId $writerKeyId -SecretAccessKey $writerSecret -Region $region

        $identity = Invoke-Aws @('sts', 'get-caller-identity', '--output', 'json')
        if ($identity.ExitCode -ne 0) {
            throw "The seed writer's credentials were rejected by AWS (sts get-caller-identity exit $($identity.ExitCode)):`n$($identity.StdErr)"
        }
        $callerArn = (($identity.StdOut | ConvertFrom-Json).Arn)
        Write-Host "Writing as $callerArn"

        foreach ($item in $planned) {
            if (-not $item.Value) {
                Write-Host "  $($item.Path): no value set ($($item.EnvName)) - skipped"
                $results.Add([pscustomobject]@{ Path = $item.Path; Status = 'skipped (no value)' })
                continue
            }

            # Read before write, so an unchanged secret doesn't mint a new
            # parameter version on every deploy. SSM keeps 100 versions per
            # parameter; a re-runnable workflow that always writes would burn
            # through them and make the version history useless as an audit
            # of when a secret actually rotated.
            $current = Invoke-Aws @('ssm', 'get-parameter', '--name', $item.Path, '--with-decryption', '--output', 'json')
            $exists = $current.ExitCode -eq 0
            if (-not $exists -and $current.StdErr -notmatch 'ParameterNotFound') {
                throw "Reading $($item.Path) failed (exit $($current.ExitCode)):`n$($current.StdErr)"
            }

            if ($exists) {
                $existingValue = ($current.StdOut | ConvertFrom-Json).Parameter.Value
                if ($existingValue -ceq $item.Value) {
                    Write-Host "  $($item.Path): unchanged"
                    $results.Add([pscustomobject]@{ Path = $item.Path; Status = 'unchanged' })
                    continue
                }
            }

            # --cli-input-json from a file, not --value on the command line:
            # arguments are visible in the process list to every other user on
            # the machine, and this runner is shared with other workflows.
            $inputFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-ssm-$([Guid]::NewGuid().ToString('N')).json"
            try {
                $payload = [ordered]@{
                    Name      = $item.Path
                    Value     = $item.Value
                    Type      = 'SecureString'
                    Overwrite = $true
                    Tier      = 'Standard'
                }
                if ($item.Description) { $payload.Description = $item.Description }
                Write-Utf8NoBom -Path $inputFile -Content ($payload | ConvertTo-Json -Depth 4)

                $put = Invoke-Aws @('ssm', 'put-parameter', '--cli-input-json', "file://$inputFile", '--output', 'json')
                if ($put.ExitCode -ne 0) {
                    throw "Writing $($item.Path) failed (exit $($put.ExitCode)):`n$($put.StdErr)"
                }
                $version = ($put.StdOut | ConvertFrom-Json).Version
                $status = if ($exists) { "updated (v$version)" } else { "created (v$version)" }
                Write-Host "  $($item.Path): $status"
                $results.Add([pscustomobject]@{ Path = $item.Path; Status = $status })
            }
            finally {
                Remove-Item $inputFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify as the ESO identity'
    # ---------------------------------------------------------------- #

    # Deliberately the read-only credential, not the writer's: this is the one
    # that will be sitting in the cluster for years, and a typo'd key or a
    # too-narrow IAM policy is far cheaper to find here than as a
    # SecretSyncedError on a resource nothing else can start without.
    Use-AwsIdentity -AccessKeyId $bootstrapValues['access-key-id'] -SecretAccessKey $bootstrapValues['secret-access-key'] -Region $region

    $listed = Invoke-Aws @('ssm', 'get-parameters-by-path', '--path', $prefix, '--recursive', '--query', 'Parameters[].Name', '--output', 'json')
    if ($listed.ExitCode -ne 0) {
        # Named explicitly because the near-miss is the likely cause and reads
        # as correct: GetParametersByPath authorizes against the *path*, so a
        # policy scoped only to '...:parameter$prefix/*' denies a call on
        # '...:parameter$prefix'. Both ARNs are needed; scripts\secrets\iam\
        # carries both, and Set-AerieSecretsIam.ps1 applies them.
        throw "The ESO credential couldn't list $prefix/* (exit $($listed.ExitCode)). The aerie-eso IAM user needs ssm:GetParametersByPath on BOTH 'arn:aws:ssm:*:*:parameter$prefix' and 'arn:aws:ssm:*:*:parameter$prefix/*' - the bare path ARN is the one usually missing. Fix: .\scripts\secrets\Set-AerieSecretsIam.ps1`n$($listed.StdErr)"
    }
    $listedNames = @($listed.StdOut | ConvertFrom-Json)
    Write-Host "ESO credential can list $($listedNames.Count) parameter(s) under $prefix."

    # Listing doesn't prove decryption: GetParametersByPath without
    # --with-decryption never touches KMS, so a missing kms:Decrypt would pass
    # the check above and fail on every real read. Fetch exactly one value to
    # close that gap, and never print it.
    $probePath = if ($withValue.Count -gt 0) { $withValue[0].Path } elseif ($listedNames.Count -gt 0) { $listedNames[0] } else { $null }
    if ($probePath) {
        $decrypt = Invoke-Aws @('ssm', 'get-parameter', '--name', $probePath, '--with-decryption', '--query', 'Parameter.Name', '--output', 'text')
        if ($decrypt.ExitCode -ne 0) {
            throw "The ESO credential couldn't decrypt $probePath (exit $($decrypt.ExitCode)). It needs kms:Decrypt on the default aws/ssm key as well as ssm:GetParameter*:`n$($decrypt.StdErr)"
        }
        Write-Host "ESO credential can decrypt SecureString values (probed $probePath)."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Bootstrap Secret'
    # ---------------------------------------------------------------- #

    if ($SkipBootstrap) {
        Write-Host '-SkipBootstrap: the cluster keeps whatever bootstrap Secret it already has.'
    }
    else {
        $namespace = Get-Field $bootstrap 'namespace'
        $secretName = Get-Field $bootstrap 'secretName'
        $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-secrets-$([Guid]::NewGuid().ToString('N')).known_hosts"
        $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

        $probe = Invoke-NodeSsh @ssh -Command 'sudo k3s kubectl version --output=json 2>/dev/null | head -n 1; sudo k3s kubectl get --raw /readyz' -ConnectTimeoutSec 20
        if ($probe.ExitCode -ne 0) {
            $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
            throw "Couldn't reach the apiserver on $IPAddress as '$Username'$(if ($permanentReason) { ": $permanentReason" }):`n$($probe.StdOut)$($probe.StdErr)"
        }

        # An apply of a literal manifest, rather than
        # `kubectl create secret --from-literal ... --dry-run=client -o yaml |
        # kubectl apply -f -`: same idempotent result in one process instead
        # of two, and no secret value in any argv. The manifest travels over
        # the SSH channel's standard input straight into `kubectl apply -f -`,
        # because sshd runs -Command through a login shell whose argv every
        # local `ps` can read.
        $dataLines = foreach ($keyName in $bootstrapValues.Keys) {
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bootstrapValues[$keyName]))
            "  ${keyName}: $encoded"
        }

        $manifest = @(
            'apiVersion: v1'
            'kind: Namespace'
            'metadata:'
            "  name: $namespace"
            '---'
            'apiVersion: v1'
            'kind: Secret'
            'metadata:'
            "  name: $secretName"
            "  namespace: $namespace"
            '  labels:'
            '    app.kubernetes.io/managed-by: aerie-provision-2'
            '  annotations:'
            # Structural breadcrumb for whoever finds this Secret and wonders
            # why it isn't an ExternalSecret like everything else.
            '    aerie.internal/why: "Bootstrap credential for the ESO ClusterSecretStore - the one secret that cannot come from the secret store it unlocks. Managed by scripts/secrets/Sync-AerieSecrets.ps1, never by Flux."'
            'type: Opaque'
            'data:'
        ) + $dataLines

        # Trailing newline on purpose: Windows PowerShell terminates a piped
        # string with CRLF, so without it the last data line would carry a
        # stray carriage return into the YAML.
        $apply = Invoke-NodeSsh @ssh -Command 'sudo k3s kubectl apply -f -' -StdIn (($manifest -join "`n") + "`n") -ConnectTimeoutSec 30
        if ($apply.ExitCode -ne 0) {
            throw "Applying the bootstrap Secret to $IPAddress failed (exit $($apply.ExitCode)):`n$($apply.StdOut)$($apply.StdErr)"
        }
        Write-Host $apply.StdOut.TrimEnd()

        # Read back key names and metadata only - never values.
        $readBack = Invoke-NodeSsh @ssh -Command "sudo k3s kubectl -n $namespace get secret $secretName -o go-template='{{.metadata.name}} type={{.type}} keys=[{{range `$k,`$v := .data}}{{`$k}} {{end}}] created={{.metadata.creationTimestamp}}'" -ConnectTimeoutSec 20
        if ($readBack.ExitCode -ne 0) {
            throw "The bootstrap Secret applied but couldn't be read back (exit $($readBack.ExitCode)):`n$($readBack.StdErr)"
        }
        Write-Host $readBack.StdOut.TrimEnd()
        $bootstrapStatus = 'applied'
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "Secrets synced in ${elapsed} min." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Next in Phase 2: flux bootstrap (Provision 3), which is what starts reconciling'
    Write-Host 'deploy/ - including the Phase 3 ClusterSecretStore that consumes the Secret above.'
    if (-not $SkipBootstrap) {
        Write-Host ''
        Write-Warning "This targeted $IPAddress directly - there is no VIP in front of the apiserver (kube-vip fronts ingress only). If that node is gone, re-run this against a surviving server; it's idempotent. Known and accepted until Phase 7; see docs/secrets-architecture.md."
    }

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $lines = @(
            "## Secrets seeded under $tick$prefix$tick"
            ''
            '| Parameter | Status |'
            '|---|---|'
        )
        foreach ($result in $results) {
            $lines += "| $tick$($result.Path)$tick | $($result.Status) |"
        }
        $lines += @(
            ''
            "ESO bootstrap Secret: **$bootstrapStatus**$(if ($bootstrapStatus -eq 'applied') { " - $tick$(Get-Field $bootstrap 'secretName')$tick in $tick$(Get-Field $bootstrap 'namespace')$tick on $tick$IPAddress$tick" })"
            ''
            "Verified with the read-only ESO credential: $($listedNames.Count) parameter(s) listed, decryption confirmed."
            ''
            '_No secret values are printed by this workflow, by design._'
        )
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
    Restore-AwsEnvironment
}
