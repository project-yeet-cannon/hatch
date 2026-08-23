<#
.SYNOPSIS
    Phase 8a step 1, end to end: the dedicated S3 bucket Longhorn backs up
    into, the 'aerie-longhorn' user scoped to it, and the parameter-tree read
    'aerie-restic' grows so 8b.7's export can dump /aerie/* into the backup
    repository. Then it proves all three against the step's exit criteria.

.DESCRIPTION
    The cluster plan Phase 8a.1 is two AWS changes, and this is both of them
    plus the check that they landed:

      1. A dedicated bucket - versioned, private, with a lifecycle rule that
         expires noncurrent versions and abandoned multipart uploads and
         touches nothing current. Same discipline 4a.2 set for the WAL bucket,
         and the same reason: Longhorn owns its own backupstore/ layout and
         expects to be the only writer, so a lifecycle rule written for one
         layout applied to another is how a restore finds a missing block.
         Nothing here deletes a current object - 8b.10's `retain: 7` is the
         only thing allowed to prune a backup.

      2. Two inline policies, delegated to Set-AerieSecretsIam.ps1 so the
         rendering engine and the committed documents in iam\ have one
         implementation. 'aerie-longhorn' gets the bucket; 'aerie-restic'
         gets a SECOND inline policy for the tree rather than an edit of the
         bucket policy Phase 0 gave it, which is not committed here and would
         be destroyed by a blind put-user-policy.

    What it deliberately does not do, matching Set-AerieSecretsIam.ps1's rule:
    create an access key. Minting one means printing one. The step that needs
    Longhorn's keys is 8b.1, which reads them from repository variables and
    secrets an operator pasted there from one unlogged terminal:

        aws iam create-access-key --user-name aerie-longhorn `
            --query 'AccessKey.[AccessKeyId,SecretAccessKey]' --output text

    Run as an operator identity that can write IAM and S3 - not as the seed
    writer, and not as either of the users this touches. Idempotent end to
    end: an existing bucket is re-configured rather than recreated, and
    put-user-policy overwrites an inline policy with the same bytes.

.PARAMETER LonghornBucket
    The bucket Longhorn's backup target names. A third dedicated bucket:
    neither the WAL bucket (4a.2) nor the restic one (Phase 0). This is the
    value cluster-config.json carries as LONGHORN_BACKUP_BUCKET in 8b.2, and
    8b.9 assembles it with AWS_REGION into 's3://<bucket>@<region>/'.

.PARAMETER LonghornUserName
    The IAM user longhorn-manager holds credentials for. Created if absent.

.PARAMETER ResticUserName
    The Phase 0 restic user, which gains the parameter-tree read. Pass ''
    to leave it alone - useful when re-running only for the bucket half.

.PARAMETER AwsRegion
    The region the bucket is created in and the region the parameter tree
    lives in. These are the same region by design: cross-region here buys
    nothing and costs egress on every backup block. Defaults to AWS_REGION.

.PARAMETER ParameterPrefix
    Overrides parameters.json's prefix. Must match what Sync-AerieSecrets.ps1
    seeds under, or the export policy scopes to a tree nobody wrote.

.PARAMETER Stage
    both        - bucket, then IAM, then verify. The normal run.
    bucket-only - skip the two policies.
    iam-only    - skip the bucket, for a policy repair.
    verify-only - change nothing; assert the exit criteria. Safe to run any
                  time, and what to run after 8b.1 mints Longhorn's keys.

.PARAMETER NoncurrentVersionExpirationDays
    How long a superseded object version survives. 30 by default, matching
    4a.2's WAL bucket. Zero disables that lifecycle rule entirely.

.PARAMETER AbortIncompleteUploadDays
    How long an abandoned multipart upload survives before S3 reclaims its
    parts. Longhorn uploads volume blocks in parts, so a manager killed
    mid-backup leaves billable garbage without this. Zero disables the rule.

.EXAMPLE
    # The whole step
    .\Set-AerieBackupAws.ps1 -LonghornBucket my-aerie-longhorn

.EXAMPLE
    # After 8b.1 put Longhorn's access key in repository variables: prove the
    # exit criteria with the identities themselves rather than the simulator
    $env:LONGHORN_AWS_ACCESS_KEY_ID = '...'; $env:LONGHORN_AWS_SECRET_ACCESS_KEY = '...'
    .\Set-AerieBackupAws.ps1 -LonghornBucket my-aerie-longhorn -Stage verify-only
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(?!.*\.\.)(?!^[0-9.]+$)[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$')]
    [string]$LonghornBucket,

    [string]$LonghornUserName = 'aerie-longhorn',

    [string]$ResticUserName = 'aerie-restic',

    [string]$MapPath = (Join-Path $PSScriptRoot 'parameters.json'),

    [ValidatePattern('^/[a-z0-9][a-z0-9\-_/]*[a-z0-9]$')]
    [string]$ParameterPrefix,

    [string]$AwsRegion,

    [ValidateSet('both', 'bucket-only', 'iam-only', 'verify-only')]
    [string]$Stage = 'both',

    [ValidateRange(0, 3650)]
    [int]$NoncurrentVersionExpirationDays = 30,

    [ValidateRange(0, 3650)]
    [int]$AbortIncompleteUploadDays = 7
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:StageNumber = 0
$script:AwsCommand = 'aws'
$script:Failures = [System.Collections.Generic.List[string]]::new()

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
        stderr-redirection reason as Set-AerieSecretsIam.ps1's copy.
    .PARAMETER Credential
        A hashtable of AWS_* environment values to run this one call under,
        for the exit checks that have to speak as a different identity. The
        surrounding environment is restored afterwards whatever happens.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [hashtable]$Credential
    )

    $saved = @{}
    $overridden = @('AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_SESSION_TOKEN', 'AWS_PROFILE')
    if ($Credential) {
        foreach ($name in $overridden) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name)
            # Cleared, not left alone: a stale AWS_PROFILE or session token in
            # the caller's environment would silently win over the pair being
            # tested, and the check would pass as the wrong identity.
            [Environment]::SetEnvironmentVariable($name, $null)
        }
        foreach ($name in $Credential.Keys) {
            [Environment]::SetEnvironmentVariable($name, $Credential[$name])
        }
    }

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
        if ($Credential) {
            foreach ($name in $overridden) {
                [Environment]::SetEnvironmentVariable($name, $saved[$name])
            }
            foreach ($name in $Credential.Keys) {
                if ($overridden -notcontains $name) {
                    [Environment]::SetEnvironmentVariable($name, $null)
                }
            }
        }
    }
}

function Invoke-AwsOrThrow {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$What)
    $result = Invoke-Aws -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "$What failed (exit $($result.ExitCode)):`n$($result.StdErr)"
    }
    $result
}

function Write-Utf8NoBom {
    <#
    .SYNOPSIS
        Writes text as UTF-8 without a byte-order mark, which is the only
        encoding the AWS CLI's file:// argument reliably parses on Windows
        PowerShell 5.1.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function Invoke-AwsWithDocument {
    <#
    .SYNOPSIS
        Runs an AWS CLI command whose last argument is a JSON document, passed
        as a temp file:// rather than inline - Windows PowerShell 5.1 mangles
        embedded quotes on the way to a native process often enough that
        inline JSON is not worth debugging twice.
    #>
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Document,
        [Parameter(Mandatory)][string]$What
    )
    $path = [IO.Path]::GetTempFileName()
    try {
        Write-Utf8NoBom -Path $path -Content $Document
        Invoke-AwsOrThrow -Arguments ($Arguments + @("file://$path")) -What $What | Out-Null
    }
    finally {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
    }
}

function Test-Assertion {
    <#
    .SYNOPSIS
        Records one exit-criterion result and prints it. Collected rather than
        thrown so a verify run reports every failure it found, not the first.
    #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Passed, [string]$Detail)
    if ($Passed) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" }
        $script:Failures.Add($Name)
    }
}

function Get-SimulatedDecision {
    <#
    .SYNOPSIS
        One simulate-principal-policy call, reduced to the set of decisions it
        returned. Needs no access key for the user being tested, which is what
        makes the exit criteria checkable before 8b.1 mints Longhorn's.

        It evaluates identity policies only. There is no bucket policy on this
        bucket by design, so that is the whole answer here - but the literal
        checks below are still the authority, and are why they run whenever
        the credentials are available.
    #>
    param(
        [Parameter(Mandatory)][string]$PrincipalArn,
        [Parameter(Mandatory)][string[]]$Actions,
        [Parameter(Mandatory)][string[]]$ResourceArns
    )
    $arguments = @('iam', 'simulate-principal-policy',
        '--policy-source-arn', $PrincipalArn,
        '--action-names') + $Actions + @('--resource-arns') + $ResourceArns + @(
        '--query', 'EvaluationResults[].EvalDecision', '--output', 'text')
    $result = Invoke-Aws -Arguments $arguments
    if ($result.ExitCode -ne 0) {
        return [pscustomobject]@{ Ok = $false; Decisions = @(); Error = $result.StdErr }
    }
    [pscustomobject]@{
        Ok        = $true
        Decisions = @($result.StdOut.Trim() -split '\s+' | Where-Object { $_ })
        Error     = ''
    }
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
    throw "No region. Pass -AwsRegion, or set AWS_REGION - it has to match the region the parameter tree lives in and the region 8b.9's backup target names."
}

$resolved = Get-Command $script:AwsCommand -ErrorAction SilentlyContinue
if (-not $resolved) {
    throw "The AWS CLI isn't on PATH. Install AWS CLI v2 - scripts\runner\Install-RunnerDependencies.ps1 -Dependency AwsCli is what the runner uses."
}
$script:AwsCommand = $resolved.Source

$identity = (Invoke-AwsOrThrow -Arguments @('sts', 'get-caller-identity', '--output', 'json') `
        -What 'Resolving the calling identity').StdOut | ConvertFrom-Json
$accountId = $identity.Account

Write-Host "AWS CLI: $script:AwsCommand"
Write-Host "Account: $accountId"
Write-Host "Caller:  $($identity.Arn)"
Write-Host "Region:  $region"
Write-Host "Prefix:  $prefix"
Write-Host "Bucket:  $LonghornBucket"
Write-Host "Stage:   $Stage"

# The one confusion that produces a working run and a broken backup: pointing
# this at a bucket another writer already owns. Both of those are declared
# keys, so this can check rather than warn.
$walBucket = $env:WAL_BUCKET
if ($walBucket -and $walBucket -eq $LonghornBucket) {
    throw "-LonghornBucket is the same bucket as WAL_BUCKET ('$LonghornBucket'). They are separate by design (4a.2, 8a.1): barman-cloud and Longhorn each assume they own the whole prefix layout."
}

# ---------------------------------------------------------------- #
if ($Stage -eq 'both' -or $Stage -eq 'bucket-only') {
    Write-Stage "Bucket $LonghornBucket"
# ---------------------------------------------------------------- #

    $head = Invoke-Aws -Arguments @('s3api', 'head-bucket', '--bucket', $LonghornBucket)
    if ($head.ExitCode -eq 0) {
        Write-Host "  exists - reconfiguring in place"
    }
    elseif ($head.StdErr -match '\(403\)|Forbidden') {
        # 403 means the name is taken by a bucket this identity cannot read -
        # possibly someone else's account entirely. Creating over it is not
        # possible and pretending otherwise wastes a run.
        throw "Bucket '$LonghornBucket' exists but this identity cannot read it (403). Either it belongs to another account, or these are not operator credentials:`n$($head.StdErr)"
    }
    else {
        Write-Host "  creating"
        $create = @('s3api', 'create-bucket', '--bucket', $LonghornBucket, '--region', $region)
        # us-east-1 is the one region that rejects LocationConstraint.
        if ($region -ne 'us-east-1') {
            $create += @('--create-bucket-configuration', "LocationConstraint=$region")
        }
        Invoke-AwsOrThrow -Arguments $create -What "Creating bucket '$LonghornBucket'" | Out-Null
    }

    # A bucket in the wrong region is a target that resolves and then fails at
    # first use, because 8b.9's 's3://bucket@region/' names the region
    # separately and nothing reconciles the two.
    $locationRaw = (Invoke-AwsOrThrow -Arguments @('s3api', 'get-bucket-location', '--bucket', $LonghornBucket, '--output', 'json') `
            -What 'Reading the bucket location').StdOut | ConvertFrom-Json
    # us-east-1 answers with a null LocationConstraint, which is not a bug.
    $location = if ($locationRaw.LocationConstraint) { $locationRaw.LocationConstraint } else { 'us-east-1' }
    if ($location -ne $region) {
        throw "Bucket '$LonghornBucket' is in $location but this run is for $region. 8b.9's backup target names the region separately, so the mismatch fails at first backup rather than here. Use a bucket in $region, or re-run with -AwsRegion $location if that is the intended home."
    }

    Invoke-AwsWithDocument -Arguments @('s3api', 'put-public-access-block', '--bucket', $LonghornBucket,
        '--public-access-block-configuration') -What 'Blocking public access' -Document @'
{
  "BlockPublicAcls": true,
  "IgnorePublicAcls": true,
  "BlockPublicPolicy": true,
  "RestrictPublicBuckets": true
}
'@
    Write-Host '  public access blocked'

    Invoke-AwsWithDocument -Arguments @('s3api', 'put-bucket-encryption', '--bucket', $LonghornBucket,
        '--server-side-encryption-configuration') -What 'Setting default encryption' -Document @'
{
  "Rules": [
    {
      "ApplyServerSideEncryptionByDefault": { "SSEAlgorithm": "AES256" },
      "BucketKeyEnabled": true
    }
  ]
}
'@
    Write-Host '  default encryption: SSE-S3'

    Invoke-AwsOrThrow -Arguments @('s3api', 'put-bucket-versioning', '--bucket', $LonghornBucket,
        '--versioning-configuration', 'Status=Enabled') -What 'Enabling versioning' | Out-Null
    Write-Host '  versioning enabled'

    # Noncurrent versions and abandoned uploads only. Nothing expires a
    # current object: 8b.10's retain is the only thing that prunes a backup,
    # and a lifecycle rule deleting underneath Longhorn produces a restore
    # that fails on a block that was there yesterday.
    $rules = [System.Collections.Generic.List[string]]::new()
    if ($NoncurrentVersionExpirationDays -gt 0) {
        $rules.Add(@"
    {
      "ID": "expire-noncurrent-versions",
      "Status": "Enabled",
      "Filter": { "Prefix": "" },
      "NoncurrentVersionExpiration": { "NoncurrentDays": $NoncurrentVersionExpirationDays }
    }
"@)
    }
    if ($AbortIncompleteUploadDays -gt 0) {
        $rules.Add(@"
    {
      "ID": "abort-incomplete-multipart-uploads",
      "Status": "Enabled",
      "Filter": { "Prefix": "" },
      "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": $AbortIncompleteUploadDays }
    }
"@)
    }
    if ($rules.Count -gt 0) {
        $document = "{`n  `"Rules`": [`n" + ($rules -join ",`n") + "`n  ]`n}"
        $null = $document | ConvertFrom-Json
        Invoke-AwsWithDocument -Arguments @('s3api', 'put-bucket-lifecycle-configuration', '--bucket', $LonghornBucket,
            '--lifecycle-configuration') -What 'Setting the lifecycle configuration' -Document $document
        Write-Host "  lifecycle: $($rules.Count) rule(s), none of which expire a current object"
    }
    else {
        Write-Host '  lifecycle: both rules disabled by parameter - skipped'
    }
}

# ---------------------------------------------------------------- #
if ($Stage -eq 'both' -or $Stage -eq 'iam-only') {
    Write-Stage 'IAM users and policies'
# ---------------------------------------------------------------- #

    # Created here rather than left manual, unlike Set-AerieSecretsIam.ps1's
    # targets: this one is new in Phase 8, and creating a user is not the
    # thing that rule protects. Minting its access key still is, and is still
    # the operator's one unlogged terminal.
    $user = Invoke-Aws -Arguments @('iam', 'get-user', '--user-name', $LonghornUserName)
    if ($user.ExitCode -ne 0) {
        if ($user.StdErr -notmatch 'NoSuchEntity') {
            throw "Reading IAM user '$LonghornUserName' failed for a reason other than its absence (exit $($user.ExitCode)):`n$($user.StdErr)"
        }
        Invoke-AwsOrThrow -Arguments @('iam', 'create-user', '--user-name', $LonghornUserName,
            '--tags', 'Key=aerie,Value=longhorn-backup') -What "Creating IAM user '$LonghornUserName'" | Out-Null
        Write-Host "  created user '$LonghornUserName'"
    }
    else {
        Write-Host "  user '$LonghornUserName' exists"
    }

    # Delegated so the render-and-attach engine, the placeholder guard and the
    # committed documents have exactly one implementation. -EsoUserName '' is
    # what keeps a backup run from re-applying the policy the whole cluster
    # reads: correct either way, but not this script's business.
    $iamScript = Join-Path $PSScriptRoot 'Set-AerieSecretsIam.ps1'
    if (-not (Test-Path $iamScript)) {
        throw "Set-AerieSecretsIam.ps1 isn't at $iamScript."
    }
    $iamParams = @{
        EsoUserName      = ''
        LonghornUserName = $LonghornUserName
        LonghornBucket   = $LonghornBucket
        MapPath          = $MapPath
        AwsRegion        = $region
        AwsAccountId     = $accountId
        ParameterPrefix  = $prefix
    }
    if ($ResticUserName) { $iamParams.ResticUserName = $ResticUserName }
    else { Write-Host '  no -ResticUserName: leaving the parameter-export read untouched' }

    & $iamScript @iamParams
}

# ---------------------------------------------------------------- #
Write-Stage 'Exit criteria'
# ---------------------------------------------------------------- #

# IAM is eventually consistent, and a policy attached two seconds ago
# simulating as implicitDeny is the classic false failure here.
if ($Stage -ne 'verify-only') {
    Write-Host 'Waiting 10s for IAM to settle before asserting.'
    Start-Sleep -Seconds 10
}

$longhornArn = "arn:aws:iam::${accountId}:user/$LonghornUserName"
$bucketArn = "arn:aws:s3:::$LonghornBucket"

# 'aws s3 ls s3://<longhorn-bucket> succeeds as aerie-longhorn', simulated.
$simulation = Get-SimulatedDecision -PrincipalArn $longhornArn `
    -Actions @('s3:ListBucket') -ResourceArns @($bucketArn)
Test-Assertion -Name "$LonghornUserName may list $LonghornBucket" `
    -Passed ($simulation.Ok -and $simulation.Decisions -and ($simulation.Decisions | Where-Object { $_ -ne 'allowed' }).Count -eq 0) `
    -Detail "decisions: $($simulation.Decisions -join ', ') $($simulation.Error)"

$simulation = Get-SimulatedDecision -PrincipalArn $longhornArn `
    -Actions @('s3:PutObject', 's3:AbortMultipartUpload', 's3:ListMultipartUploadParts') `
    -ResourceArns @("$bucketArn/backupstore/probe")
Test-Assertion -Name "$LonghornUserName may write and abort multipart uploads" `
    -Passed ($simulation.Ok -and $simulation.Decisions -and ($simulation.Decisions | Where-Object { $_ -ne 'allowed' }).Count -eq 0) `
    -Detail "decisions: $($simulation.Decisions -join ', ') $($simulation.Error)"

if ($ResticUserName) {
    $resticArn = "arn:aws:iam::${accountId}:user/$ResticUserName"

    # '...and fails as aerie-restic'. The isolation half, and the check that
    # the two credentials did not quietly get pointed at one bucket.
    $simulation = Get-SimulatedDecision -PrincipalArn $resticArn `
        -Actions @('s3:ListBucket', 's3:PutObject') -ResourceArns @($bucketArn, "$bucketArn/*")
    Test-Assertion -Name "$ResticUserName may NOT reach $LonghornBucket" `
        -Passed ($simulation.Ok -and $simulation.Decisions -and ($simulation.Decisions | Where-Object { $_ -eq 'allowed' }).Count -eq 0) `
        -Detail "decisions: $($simulation.Decisions -join ', ') $($simulation.Error)"

    # Both ARNs, because only the second one is obvious and only the first one
    # is the bug: GetParametersByPath authorizes against the bare path.
    $simulation = Get-SimulatedDecision -PrincipalArn $resticArn `
        -Actions @('ssm:GetParametersByPath') `
        -ResourceArns @("arn:aws:ssm:${region}:${accountId}:parameter$prefix")
    Test-Assertion -Name "$ResticUserName may list the tree at the bare path ARN" `
        -Passed ($simulation.Ok -and $simulation.Decisions -and ($simulation.Decisions | Where-Object { $_ -ne 'allowed' }).Count -eq 0) `
        -Detail "decisions: $($simulation.Decisions -join ', ') $($simulation.Error)"

    $simulation = Get-SimulatedDecision -PrincipalArn $resticArn `
        -Actions @('ssm:GetParameters') `
        -ResourceArns @("arn:aws:ssm:${region}:${accountId}:parameter$prefix/backup/restic-password")
    Test-Assertion -Name "$ResticUserName may read a parameter under the tree" `
        -Passed ($simulation.Ok -and $simulation.Decisions -and ($simulation.Decisions | Where-Object { $_ -ne 'allowed' }).Count -eq 0) `
        -Detail "decisions: $($simulation.Decisions -join ', ') $($simulation.Error)"
}

# The literal exit text, when the credentials to speak as each user are here.
# The simulator does not evaluate a bucket policy or an SCP; these do.
if ($env:LONGHORN_AWS_ACCESS_KEY_ID -and $env:LONGHORN_AWS_SECRET_ACCESS_KEY) {
    $ls = Invoke-Aws -Arguments @('s3', 'ls', "s3://$LonghornBucket") -Credential @{
        AWS_ACCESS_KEY_ID     = $env:LONGHORN_AWS_ACCESS_KEY_ID
        AWS_SECRET_ACCESS_KEY = $env:LONGHORN_AWS_SECRET_ACCESS_KEY
        AWS_DEFAULT_REGION    = $region
    }
    Test-Assertion -Name "aws s3 ls succeeds with $LonghornUserName's own keys" `
        -Passed ($ls.ExitCode -eq 0) -Detail $ls.StdErr
}
else {
    Write-Host "  SKIP  literal check as $LonghornUserName - set LONGHORN_AWS_ACCESS_KEY_ID and LONGHORN_AWS_SECRET_ACCESS_KEY to run it"
}

if ($ResticUserName -and $env:RESTIC_AWS_ACCESS_KEY_ID -and $env:RESTIC_AWS_SECRET_ACCESS_KEY) {
    $resticCredential = @{
        AWS_ACCESS_KEY_ID     = $env:RESTIC_AWS_ACCESS_KEY_ID
        AWS_SECRET_ACCESS_KEY = $env:RESTIC_AWS_SECRET_ACCESS_KEY
        AWS_DEFAULT_REGION    = $region
    }

    $ls = Invoke-Aws -Arguments @('s3', 'ls', "s3://$LonghornBucket") -Credential $resticCredential
    Test-Assertion -Name "aws s3 ls fails with $ResticUserName's own keys" `
        -Passed ($ls.ExitCode -ne 0) -Detail 'It succeeded, which means the two backup writers share a bucket.'

    # The count, not the values. --with-decryption is what proves kms:Decrypt
    # actually resolves, and printing the tree would put every secret in this
    # phase into a run log.
    $tree = Invoke-Aws -Arguments @('ssm', 'get-parameters-by-path', '--path', $prefix,
        '--recursive', '--with-decryption', '--query', 'length(Parameters)', '--output', 'text') `
        -Credential $resticCredential
    $count = 0
    $parsed = $tree.ExitCode -eq 0 -and [int]::TryParse($tree.StdOut.Trim(), [ref]$count)
    Test-Assertion -Name "$ResticUserName decrypts the tree ($count parameter(s))" `
        -Passed ($parsed -and $count -gt 0) -Detail $tree.StdErr
}
elseif ($ResticUserName) {
    Write-Host "  SKIP  literal check as $ResticUserName - set RESTIC_AWS_ACCESS_KEY_ID and RESTIC_AWS_SECRET_ACCESS_KEY to run it"
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    throw "Phase 8a.1 is not met: $($script:Failures.Count) assertion(s) failed - $($script:Failures -join '; ')."
}
Write-Host 'Phase 8a.1 exit criteria met.' -ForegroundColor Green
Write-Host ''
Write-Host "Still manual, and deliberately: $LonghornUserName's access key. Mint it in one unlogged terminal and paste the pair into repository variables and secrets for 8b.1:"
Write-Host "  aws iam create-access-key --user-name $LonghornUserName --query 'AccessKey.[AccessKeyId,SecretAccessKey]' --output text"
