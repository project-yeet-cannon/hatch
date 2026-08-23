<#
.SYNOPSIS
    Renders and applies the IAM policies Provision 2, Phase 4 and Phase 8
    depend on - the read-only 'aerie-eso' policy, the seed writer's, CNPG's
    WAL archiving user, Longhorn's backup target user, and the parameter-tree
    read 'aerie-restic' grows for Phase 8's export - from the committed
    documents in iam\, scoped to this account, region, parameter prefix and
    (for the two bucket users) their bucket.

.DESCRIPTION
    The policies in iam\*.policy.json are the structural half: identical for
    every installation, with <AWS_REGION>, <AWS_ACCOUNT_ID> and
    <PARAMETER_PREFIX> left as placeholders so no account-specific value is
    committed. This script fills them in and puts them as inline policies.

    It exists because the policy is easy to get subtly wrong in a way that
    only surfaces at Provision 2's verify stage. Scoping the ESO user to
    '<prefix>/*' alone reads as correct and fails, because GetParametersByPath
    authorizes against the *path* - 'arn:...:parameter/aerie', with no
    trailing slash - which '<prefix>/*' does not match. Both ARNs are needed,
    and iam\aerie-eso.policy.json carries both.

    What it deliberately does not do is create users or access keys. A script
    that minted a credential would have to print it, and Aerie's ethos keeps
    secret bytes out of logs as firmly as out of git. Create the users once in
    the console, then point this at them.

    Idempotent: put-user-policy overwrites an inline policy of the same name,
    so re-running after changing the prefix is the way to move the tree.

.PARAMETER EsoUserName
    The read-only user held by the cluster as the ESO bootstrap Secret.

.PARAMETER SeedWriterUserName
    The user behind SSM_AWS_ACCESS_KEY_ID. Omit to leave the writer's policy
    alone - useful when only the ESO half needs repair.

.PARAMETER CnpgUserName
    The user CloudNativePG's Barman Cloud Plugin holds for WAL archiving and
    base backups (the cluster plan Phase 4a.3/4b.2). Omit to leave its policy
    alone. Requires -WalBucket.

.PARAMETER WalBucket
    The S3 bucket -CnpgUserName is scoped to - a dedicated bucket, not the
    restic one, per 4a.2. Required when -CnpgUserName is passed.

.PARAMETER LonghornUserName
    The user longhorn-manager holds for its backup target (the cluster plan
    Phase 8a.1/8b.9). Omit to leave its policy alone. Requires
    -LonghornBucket.

.PARAMETER LonghornBucket
    The S3 bucket -LonghornUserName is scoped to. A third dedicated bucket:
    Longhorn owns its own backupstore/ layout and expects to be the only
    writer, so it is neither the WAL bucket nor the restic one. Required when
    -LonghornUserName is passed. This is the value cluster-config.json carries
    as LONGHORN_BACKUP_BUCKET.

.PARAMETER ResticUserName
    The Phase 0 restic user, which Phase 8a.1 grows a parameter-tree read on
    so 8b.7's export can dump /aerie/* into the backup repository. Omit to
    leave it alone.

    This attaches a SECOND inline policy ('aerie-parameter-export-read')
    rather than editing the bucket policy that user already carries. Inline
    policies union, put-user-policy overwrites by name, and Phase 0's
    document is not committed here - so rewriting it from this script would
    replace a policy nobody has a copy of and silently break every backup.

.PARAMETER ParameterPrefix
    Overrides parameters.json's prefix. Must match what Sync-AerieSecrets.ps1
    and the Phase 3 ClusterSecretStore use, or ESO reads an empty tree.

.PARAMETER AwsAccountId
    The 12-digit account the policies name. Normally discovered from
    sts:GetCallerIdentity; pass it to skip that call, which is what makes
    -Render work on a machine with no AWS CLI and no credentials at all.

.PARAMETER Render
    Print the rendered policies and exit without calling IAM. What to use when
    the operator credentials that can write IAM aren't the ones on this
    machine - paste the output into the console instead. With -AwsAccountId
    and -AwsRegion it needs nothing installed but PowerShell.

.EXAMPLE
    # Repair the ESO policy in place, as an operator identity that can write IAM
    .\Set-AerieSecretsIam.ps1

.EXAMPLE
    # Both users, explicit region
    .\Set-AerieSecretsIam.ps1 -SeedWriterUserName aerie-ssm-seed -AwsRegion us-east-1

.EXAMPLE
    # Just show me the JSON to paste - no AWS CLI, no credentials
    .\Set-AerieSecretsIam.ps1 -Render -AwsAccountId 123456789012 -AwsRegion us-east-1

.EXAMPLE
    # The CNPG WAL archiving user, scoped to its own bucket
    .\Set-AerieSecretsIam.ps1 -CnpgUserName aerie-cnpg -WalBucket my-aerie-cnpg-wal

.EXAMPLE
    # Phase 8a.1's two policies, and nothing else - an empty -EsoUserName is
    # how to skip the default target. This is what Set-AerieBackupAws.ps1 calls.
    .\Set-AerieSecretsIam.ps1 -EsoUserName '' `
        -LonghornUserName aerie-longhorn -LonghornBucket my-aerie-longhorn `
        -ResticUserName aerie-restic
#>
[CmdletBinding()]
param(
    [string]$EsoUserName = 'aerie-eso',

    [string]$SeedWriterUserName,

    [string]$CnpgUserName,

    [string]$WalBucket,

    [string]$LonghornUserName,

    [string]$LonghornBucket,

    [string]$ResticUserName,

    [string]$MapPath = (Join-Path $PSScriptRoot 'parameters.json'),

    [ValidatePattern('^/[a-z0-9][a-z0-9\-_/]*[a-z0-9]$')]
    [string]$ParameterPrefix,

    [string]$AwsRegion,

    [ValidatePattern('^\d{12}$')]
    [string]$AwsAccountId,

    [switch]$Render
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CnpgUserName -and -not $WalBucket) {
    throw '-CnpgUserName needs -WalBucket: the policy scopes to one bucket and nothing else.'
}
if ($WalBucket -and -not $CnpgUserName) {
    throw '-WalBucket has no effect without -CnpgUserName.'
}
if ($LonghornUserName -and -not $LonghornBucket) {
    throw '-LonghornUserName needs -LonghornBucket: the policy scopes to one bucket and nothing else.'
}
if ($LonghornBucket -and -not $LonghornUserName) {
    throw '-LonghornBucket has no effect without -LonghornUserName.'
}
if ($LonghornBucket -and $LonghornBucket -eq $WalBucket) {
    throw "-LonghornBucket and -WalBucket are both '$LonghornBucket'. They are separate buckets by design (4a.2, 8a.1): each of these two writers assumes it owns the whole prefix layout."
}

$script:StageNumber = 0
$script:AwsCommand = 'aws'

function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Invoke-Aws {
    <#
    .SYNOPSIS
        Runs one AWS CLI command and returns its exit code and output without
        throwing - the same shape and the same Windows PowerShell 5.1
        stderr-redirection reason as Sync-AerieSecrets.ps1's copy.
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
        Writes text as UTF-8 without a byte-order mark, which is the only
        encoding the AWS CLI's file:// argument reliably parses on Windows
        PowerShell 5.1.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------- #
Write-Stage 'Preflight'
# ---------------------------------------------------------------- #

if (-not (Test-Path $MapPath)) {
    throw "The parameter map isn't at $MapPath. Pass -MapPath, or run this from scripts\secrets\."
}
$map = Get-Content -Path $MapPath -Raw | ConvertFrom-Json

$prefix = if ($ParameterPrefix) { $ParameterPrefix } else { $map.prefix }
if (-not $prefix) {
    throw "No parameter prefix: $MapPath has no 'prefix' and -ParameterPrefix wasn't passed."
}

$region = $AwsRegion
if (-not $region) { $region = $env:AWS_REGION }
if (-not $region) { $region = $env:AWS_DEFAULT_REGION }
if (-not $region) {
    throw "No region. Pass -AwsRegion, or set AWS_REGION - it has to match the repository variable Provision 2 runs with, because the parameter tree lives in exactly one region."
}

# Rendering is pure string substitution, so the CLI is only needed to apply,
# or to discover the account id when it wasn't given. Requiring it to print
# JSON would defeat -Render's purpose - it exists for the machine that has no
# IAM-capable credentials, which is usually the machine with no AWS CLI.
$needsCli = (-not $Render) -or (-not $AwsAccountId)
if ($needsCli) {
    $resolved = Get-Command $script:AwsCommand -ErrorAction SilentlyContinue
    if (-not $resolved) {
        $hint = if ($Render) { " To render without it, pass -AwsAccountId and -AwsRegion." } else { '' }
        throw "The AWS CLI isn't on PATH. Install AWS CLI v2 - scripts\runner\ pins the version the runner uses.$hint"
    }
    $script:AwsCommand = $resolved.Source
    Write-Host "AWS CLI: $script:AwsCommand"
}

if ($AwsAccountId) {
    $accountId = $AwsAccountId
    Write-Host "Account: $accountId (from -AwsAccountId)"
}
else {
    $identity = Invoke-Aws @('sts', 'get-caller-identity', '--output', 'json')
    if ($identity.ExitCode -ne 0) {
        throw "Couldn't resolve the calling identity (exit $($identity.ExitCode)). These have to be operator credentials that can read IAM, not the seed writer's:`n$($identity.StdErr)"
    }
    $caller = $identity.StdOut | ConvertFrom-Json
    $accountId = $caller.Account
    Write-Host "Account: $accountId"
    Write-Host "Caller:  $($caller.Arn)"
}
Write-Host "Region:  $region"
Write-Host "Prefix:  $prefix"

# ---------------------------------------------------------------- #
Write-Stage 'Render'
# ---------------------------------------------------------------- #

$targets = [System.Collections.Generic.List[object]]::new()
# The one target with a default, because repairing it is why this script
# exists. Pass -EsoUserName '' to skip it - what a caller wanting only the
# Phase 8 pair does, so a backup provisioning run does not silently re-apply
# the credential the whole cluster reads.
if ($EsoUserName) {
    $targets.Add([pscustomobject]@{
            UserName   = $EsoUserName
            PolicyName = 'aerie-secrets-read'
            FileName   = 'aerie-eso.policy.json'
        })
}
else {
    Write-Host "No -EsoUserName: leaving the ESO read policy untouched."
}
if ($SeedWriterUserName) {
    $targets.Add([pscustomobject]@{
            UserName   = $SeedWriterUserName
            PolicyName = 'aerie-secrets-seed'
            FileName   = 'seed-writer.policy.json'
        })
}
else {
    Write-Host 'No -SeedWriterUserName: leaving the writer policy untouched.'
}
if ($CnpgUserName) {
    $targets.Add([pscustomobject]@{
            UserName   = $CnpgUserName
            PolicyName = 'aerie-cnpg-wal-s3'
            FileName   = 'aerie-cnpg.policy.json'
        })
}
else {
    Write-Host 'No -CnpgUserName: leaving the CNPG WAL policy untouched.'
}
if ($LonghornUserName) {
    $targets.Add([pscustomobject]@{
            UserName   = $LonghornUserName
            PolicyName = 'aerie-longhorn-backup-s3'
            FileName   = 'aerie-longhorn.policy.json'
        })
}
else {
    Write-Host 'No -LonghornUserName: leaving the Longhorn backup policy untouched.'
}
if ($ResticUserName) {
    $targets.Add([pscustomobject]@{
            UserName   = $ResticUserName
            PolicyName = 'aerie-parameter-export-read'
            FileName   = 'aerie-restic-ssm.policy.json'
        })
}
else {
    Write-Host 'No -ResticUserName: leaving the parameter-export read untouched.'
}

if ($targets.Count -eq 0) {
    throw 'Every target was skipped, so there is nothing to do. Name at least one user.'
}

foreach ($target in $targets) {
    $path = Join-Path $PSScriptRoot (Join-Path 'iam' $target.FileName)
    if (-not (Test-Path $path)) {
        throw "Missing policy document $path."
    }
    $document = (Get-Content -Path $path -Raw).
        Replace('<AWS_REGION>', $region).
        Replace('<AWS_ACCOUNT_ID>', $accountId).
        Replace('<PARAMETER_PREFIX>', $prefix).
        Replace('<WAL_BUCKET>', [string]$WalBucket).
        Replace('<LONGHORN_BUCKET>', [string]$LonghornBucket)

    # Catches a placeholder renamed in the JSON but not here, which would
    # otherwise be applied verbatim and deny everything at runtime.
    if ($document -match '<[A-Z_]+>') {
        throw "$($target.FileName) still has an unsubstituted placeholder ($($Matches[0])) after rendering."
    }
    # Proves the result is still valid JSON before IAM sees it.
    $null = $document | ConvertFrom-Json

    Add-Member -InputObject $target -NotePropertyName 'Document' -NotePropertyValue $document
    Write-Host "$($target.FileName) -> user '$($target.UserName)', inline policy '$($target.PolicyName)'"
}

if ($Render) {
    foreach ($target in $targets) {
        Write-Host ''
        Write-Host "--- $($target.UserName) / $($target.PolicyName) ---" -ForegroundColor Yellow
        Write-Host $target.Document
    }
    Write-Host ''
    Write-Host '-Render: nothing was applied.'
    return
}

# ---------------------------------------------------------------- #
Write-Stage 'Apply'
# ---------------------------------------------------------------- #

foreach ($target in $targets) {
    # Fail on a missing user rather than creating one: a user created here
    # would still need an access key, and minting one means printing it.
    $user = Invoke-Aws @('iam', 'get-user', '--user-name', $target.UserName, '--output', 'json')
    if ($user.ExitCode -ne 0) {
        throw "IAM user '$($target.UserName)' doesn't exist or isn't readable (exit $($user.ExitCode)). Create it in the console first, then re-run:`n$($user.StdErr)"
    }

    $documentFile = [IO.Path]::GetTempFileName()
    try {
        Write-Utf8NoBom -Path $documentFile -Content $target.Document
        $put = Invoke-Aws @(
            'iam', 'put-user-policy',
            '--user-name', $target.UserName,
            '--policy-name', $target.PolicyName,
            '--policy-document', "file://$documentFile"
        )
        if ($put.ExitCode -ne 0) {
            throw "Attaching '$($target.PolicyName)' to '$($target.UserName)' failed (exit $($put.ExitCode)):`n$($put.StdErr)"
        }
        Write-Host "  $($target.UserName): inline policy '$($target.PolicyName)' applied"
    }
    finally {
        Remove-Item $documentFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'Done. IAM is eventually consistent, so give it a few seconds before re-running Provision 2.'
