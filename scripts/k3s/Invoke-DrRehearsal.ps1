<#
.SYNOPSIS
    Restores the `aerie` and `quartz` databases out of a restic snapshot into
    a throwaway Postgres, and reads the parameter tree back out of the same
    snapshot - against both repositories, in one command. The cluster plan
    Phase 8b.15, as a script rather than as a procedure someone follows.

.DESCRIPTION
    8b.15 was written expecting a person to follow docs/disaster-recovery.md
    literally and find what it left out. This is the other way to satisfy it,
    and it is the way that survives: **in a crisis nobody should be reading
    steps.** A restore path that is exercised by running a command is
    exercised the same way every quarter, by whoever is holding the pager,
    at 3am, without judgement. A restore path that is exercised by reading is
    exercised as well as the reader is rested.

    So the document keeps its four procedures - they are what you need when
    this script is itself what is missing - and this becomes the thing that
    proves the two data-recovery ones still work.

    **Scope: data recovery.** Two of the mechanisms Phase 8 built, both out of
    one snapshot:

      1. The logical database backup. `restic restore` of the newest `daily`
         snapshot, `pg_restore` of both dumps into a Postgres this script
         stands up inside the Job's own /tmp and tears down after, and a table
         count for each - compared against the live database rather than
         against a number written down here.
      2. The `/aerie/*` parameter export (8b.7). Every parameter name the
         snapshot carries, checked as a superset of every `required: true`
         entry in scripts/secrets/parameters.json, plus a proof that the
         `restic-password` *inside* the snapshot is the password that opened
         it - by SHA-256, so the value is never printed.

    The Longhorn volume restore (procedure 3) is deliberately not here. It
    restores infrastructure rather than data, it is the one procedure that has
    been exercised end to end (docs/disaster-recovery.md says so, on
    2026-08-24), and it wants its own script.

    **Nothing here writes to a repository or to a live database.** restic is
    run as `snapshots` and `restore`; neither takes the exclusive lock that
    `forget --prune` does, so a run at 03:10 exactly cannot collide with the
    nightly backup. The only contact with production is one `SELECT count(*)`
    against information_schema on the live `aerie` database, which is how the
    restored table count gets something to be compared to.

    **The restore-to-live path is not here either, and that is a decision.**
    deploy/cluster/data/schema/restore-job.yaml is the object that replays
    these same dumps over the running databases; it stays a suspended Job that
    a person un-suspends deliberately. A rehearsal that could restore over
    production by getting an argument wrong is a worse risk than the one it
    retires.

    Same three properties every gate in this directory states:

    **It does not stop at the first failure.** One repository answering and
    the other not is the single most useful thing this can tell you, and it
    can only tell you that by trying both.

    **A check it cannot evaluate is a failure, not a skip.** A Job that did
    not run to completion is "not proven", which is a failure.

    **Expectations come from the cluster and the repository, not from
    parameters.** The Job is built from the live aerie-backup CronJob's own
    pod template, so the image, the `restic` Secret, the repository strings
    and the SMB mount are whatever the nightly backup actually runs with. A
    rehearsal that restated any of them could pass against a repository
    nothing writes to any more, which is the exact failure it exists to catch.

    Stages:
      1. Preflight  - the SSH key resolves, the node answers 22, parameters.json parses.
      2. Probe      - one round trip for the CronJob pod template and the live table count.
      3. Rehearse   - one Job: restore, pg_restore, count, read the parameter export back.
      4. Checks     - per repository, evaluated against that output.
      5. Report     - one table, one exit code.

.PARAMETER IPAddress
    A k3s server's LAN address. Any server: everything read here is cluster
    state.

.PARAMETER Repository
    Which half of the pair to rehearse. `Both` is the point - the local copy
    on the house share is the one that survives losing the AWS account, and
    the S3 copy is the one that survives losing the house, so a rehearsal of
    either alone proves half of the design. `Local` or `S3` exist for a
    re-run against the one that just failed.

.PARAMETER KeepJob
    Leave the Job in place after the run instead of deleting it. For reading
    the full log with kubectl when a check fails in a way this script's
    summary does not explain. It carries a 1800s TTL either way.

.EXAMPLE
    .\Invoke-DrRehearsal.ps1 -IPAddress 192.168.1.240 -SshPrivateKeyPath ~/.ssh/aerie_node

.EXAMPLE
    # The quarterly run, against the copy that is not in AWS.
    .\Invoke-DrRehearsal.ps1 -IPAddress 192.168.1.240 -Repository Local -SshPrivateKeyPath ~/.ssh/aerie_node
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [ValidateSet('Both', 'Local', 'S3')]
    [string]$Repository = 'Both',

    [string]$ParametersPath = (Join-Path $PSScriptRoot '..\secrets\parameters.json'),

    [string]$Username = 'aerie',

    [switch]$KeepJob,

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')

# 8b.5's staging path is fixed precisely so that a name can be asserted, and
# the export lands beside the dumps in the same snapshot (8b.7) rather than in
# one of its own - which is what lets a single restore prove both mechanisms.
$ExpectedSnapshotFiles = @('aerie.dump', 'quartz.dump', 'parameters.json')

# The parameter whose value the snapshot must contain a matching copy of. This
# is the root-of-trust claim in docs/disaster-recovery.md, stated as an
# assertion: the offline password opens the box that contains a copy of
# itself, and if that stops being true the document is wrong in the one place
# it cannot afford to be.
$SelfReferentialParameter = 'backup/restic-password'

$JobName = 'aerie-dr-rehearsal'

# The Job restores a whole snapshot twice and stands up a Postgres between the
# two, so it is minutes rather than seconds. The wait is generous on purpose:
# a rehearsal that times out while the restore is still progressing reports a
# working backup as a broken one, which is the expensive direction to be wrong
# in.
$JobTimeoutSeconds = 1500

$script:StageNumber = 0
$startedUtc = (Get-Date).ToUniversalTime()

function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

function Get-Field {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Path {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Path)
    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        $current = Get-Field $current $segment
    }
    return $current
}

$script:Checks = New-Object Collections.Generic.List[psobject]
function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('Pass', 'Fail', 'Warn')][string]$Status,
        [string]$Detail = ''
    )
    $script:Checks.Add([pscustomobject]@{ Scope = $Scope; Check = $Name; Result = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'Pass' { 'DarkGray' } 'Warn' { 'Yellow' } default { 'Red' } }
    $marker = switch ($Status) { 'Pass' { 'ok  ' } 'Warn' { 'warn' } default { 'FAIL' } }
    Write-Host ("  [{0}] {1,-6} {2}{3}" -f $marker, $Scope, $Name, $(if ($Detail) { " - $Detail" })) -ForegroundColor $colour
}

function Get-RehearsalSection {
    <#
    .SYNOPSIS
        The `key=value` lines the Job emitted between one `=== repo <name>`
        banner and the next, as a hashtable, plus the `param:` lines as a
        list.

    .DESCRIPTION
        A parser rather than ConvertFrom-Json because the Job writes this
        incrementally as each step finishes: a restore that dies half way
        through still leaves everything up to that point readable, where a
        single JSON document at the end would leave nothing at all. The
        failure mode this runs in is the one where partial output is the
        whole diagnosis.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$Name
    )
    $values = @{}
    $parameters = New-Object Collections.Generic.List[string]
    $errors = New-Object Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in @($Output -split "`r?`n")) {
        $trimmed = $line.TrimEnd()
        if ($trimmed -match '^=== repo (.+)$') {
            $inSection = ($Matches[1] -eq $Name)
            continue
        }
        if (-not $inSection) { continue }
        if ($trimmed -match '^param:(.+)$') { $parameters.Add($Matches[1].Trim()); continue }
        if ($trimmed -match '^error:(.+)$') { $errors.Add($Matches[1].Trim()); continue }
        if ($trimmed -match '^([a-z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim() }
    }
    return [pscustomobject]@{
        Values     = $values
        Parameters = @($parameters)
        Errors     = @($errors)
        Found      = ($values.Count -gt 0 -or $errors.Count -gt 0)
    }
}

function Get-SectionValue {
    param([Parameter(Mandatory)]$Section, [Parameter(Mandatory)][string]$Key)
    if ($Section.Values.ContainsKey($Key)) { return [string]$Section.Values[$Key] }
    return $null
}

function Get-SectionInt {
    <#
    .SYNOPSIS
        One emitted value as an integer, or $null if it is absent or is not
        one - "the Job printed something that is not a count" and "the Job
        printed nothing" are the same verdict here, and both are failures.
    #>
    param([Parameter(Mandatory)]$Section, [Parameter(Mandatory)][string]$Key)
    $raw = Get-SectionValue -Section $Section -Key $Key
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $parsed = 0
    if ([int]::TryParse($raw, [ref]$parsed)) { return $parsed }
    return $null
}

$tempKeyFile = $null
$knownHostsFile = $null

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    if (-not (Test-Path $ParametersPath -PathType Leaf)) {
        $failures.Add("Not found: '$ParametersPath'. Run this from a checkout of the repository the cluster reconciles from - the expected parameter names come from it.")
    }
    if (-not $SshPrivateKey -and -not $SshPrivateKeyPath) {
        $failures.Add('No SSH private key: pass -SshPrivateKeyPath or -SshPrivateKey.')
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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-dr-rehearsal'
            $privateKeyPath = $resolvedKey.Path
            $tempKeyFile = $resolvedKey.TempFile
            $keyFingerprint = $resolvedKey.Fingerprint
        }
        catch { $failures.Add($_.Exception.Message) }
    }

    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 22)) {
        $failures.Add("$IPAddress isn't answering on port 22. Confirm the node is up and -IPAddress is right.")
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    # The expected parameter names, built from the committed map rather than
    # listed here - 4b.11's rule, and the reason this script needs a checkout
    # rather than just a cluster. Only `required: true` entries: the optional
    # ones are optional precisely because an installation may never have
    # seeded them, so their absence from an export is not a finding.
    $parametersDocument = Get-Content -Path $ParametersPath -Raw | ConvertFrom-Json
    $parameterPrefix = ([string](Get-Field $parametersDocument 'prefix')).TrimEnd('/')
    $requiredParameterNames = @(
        Get-Field $parametersDocument 'parameters' |
            Where-Object { (Get-Field $_ 'required') -eq $true } |
            ForEach-Object { "$parameterPrefix/$(Get-Field $_ 'key')" }
    )
    if ($requiredParameterNames.Count -eq 0) {
        throw "No 'required: true' parameters in '$ParametersPath' - that file is the source of this run's expectations and it appears empty or reshaped."
    }

    # Local first: it is the copy that is not in AWS, so it is the one whose
    # failure would be invisible to every AWS-side alarm, and the one worth
    # having the answer for even if the run is interrupted.
    $repositoriesToRehearse = switch ($Repository) {
        'Local' { @('local') }
        'S3' { @('s3') }
        default { @('local', 's3') }
    }

    Write-Host "Cluster:     $IPAddress"
    Write-Host "Repo:        $((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)"
    if ($keyFingerprint) { Write-Host "SSH key:     $keyFingerprint" }
    Write-Host "Rehearsing:  $($repositoriesToRehearse -join ', ')"
    Write-Host "Expecting:   $($requiredParameterNames.Count) required parameter(s) under $parameterPrefix"
    Write-Host 'Preflight OK. One Job restores each repository into its own scratch Postgres and deletes it; nothing writes to a repository or to a live database.'

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-dr-rehearsal-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Probe'
    # ---------------------------------------------------------------- #

    $probe = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
        'sudo k3s kubectl -n aerie get cronjob aerie-backup -o json 2>/dev/null || echo {}'
    )
    if ($probe.ExitCode -ne 0) {
        throw "Could not read the aerie-backup CronJob from $IPAddress : $($probe.StdErr.Trim())"
    }
    $backupCronJob = $null
    try { $backupCronJob = $probe.StdOut | ConvertFrom-Json } catch { }

    $template = Get-Path $backupCronJob 'spec.jobTemplate.spec.template'
    if ($null -eq $template) {
        throw 'The aerie-backup CronJob has no pod template to build a rehearsal from - it is missing or unreadable. Without it this script would have to restate the repository strings and the credential, and a rehearsal that restates them can pass against a repository nothing writes to.'
    }
    Write-Host 'Read the aerie-backup pod template: image, restic Secret, both repository strings and the SMB mount come from it.'

    # ---------------------------------------------------------------- #
    Write-Stage 'Rehearse'
    # ---------------------------------------------------------------- #

    # The rehearsal itself, as the Job's command. Written to keep going after
    # a failure rather than `set -e`: one repository answering and the other
    # not is the most useful thing this run can report, and it can only report
    # it by attempting both. Every step that can fail says so on its own
    # `error:` line and the section it belongs to is still parseable.
    #
    # Emitted as `key=value` lines rather than one JSON document at the end,
    # for the reason Get-RehearsalSection gives: a Job killed half way through
    # still leaves everything up to that point readable, and the half-finished
    # case is the one this is for.
    $rehearsalScript = @'
set -u

PREFIX="${PARAMETER_PREFIX:-/aerie}"

# The hash of the password that is about to open the repositories. Compared
# below against the hash of the copy of itself the snapshot carries - which is
# the root-of-trust claim in docs/disaster-recovery.md, and the one assertion
# here that must never print what it is asserting about. `printf %s` rather
# than `echo`, which would add the newline the exported value does not have.
WANT_HASH=$(printf '%s' "$RESTIC_PASSWORD" | sha256sum | cut -d" " -f1)

# The number to compare a restored table count against, read from the live
# database rather than written down here - the same rule the gates use, and
# the reason a schema migration does not turn into a failing rehearsal three
# months later. Read-only: one count over information_schema.
LIVE_TABLES=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d aerie -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('public','storage')" 2>/dev/null | tr -d " ")

rehearse() {
  LABEL="$1"
  REPO="$2"
  echo "=== repo $LABEL"

  if [ -z "$REPO" ]; then
    echo "error:the aerie-backup pod template carries no repository string for this half of the pair"
    return
  fi
  echo "repository=$REPO"
  echo "live_aerie_tables=${LIVE_TABLES:-}"

  SCRATCH=$(mktemp -d) || { echo "error:could not create a scratch directory"; return; }
  PGDATA="$SCRATCH/pgdata"

  # 1. Which snapshot, and when. `--tag daily` because that is the tag 8b.5
  #    writes and the one docs/disaster-recovery.md names; `latest` alone
  #    would happily select the cutover-final archive.
  SNAPSHOT_JSON=$(restic -r "$REPO" snapshots latest --tag daily --json 2>&1)
  if [ $? -ne 0 ]; then
    echo "error:restic could not list snapshots: $(echo "$SNAPSHOT_JSON" | tr "\n" " " | cut -c1-300)"
    rm -rf "$SCRATCH"
    return
  fi
  echo "snapshot_id=$(echo "$SNAPSHOT_JSON" | jq -r '.[-1].short_id // empty')"
  echo "snapshot_time=$(echo "$SNAPSHOT_JSON" | jq -r '.[-1].time // empty')"

  # 2. The restore. The whole snapshot rather than three `restic dump`s: this
  #    is the procedure the weekly verify Job runs and the one the document
  #    describes, and restoring all three files together is what proves they
  #    are in fact all three in the same snapshot.
  RESTORE_OUT=$(restic -r "$REPO" restore latest --tag daily --target "$SCRATCH/restored" 2>&1)
  if [ $? -ne 0 ]; then
    echo "error:restore failed: $(echo "$RESTORE_OUT" | tr "\n" " " | cut -c1-300)"
    rm -rf "$SCRATCH"
    return
  fi

  # Found by name, not by path, for the reason cluster-verify.sh gives: the
  # snapshot records the absolute staging path cluster-backup.sh wrote from,
  # and this should not have to agree with that string to work against an
  # older snapshot.
  AERIE_DUMP=$(find "$SCRATCH/restored" -name aerie.dump | head -1)
  QUARTZ_DUMP=$(find "$SCRATCH/restored" -name quartz.dump | head -1)
  PARAMS_FILE=$(find "$SCRATCH/restored" -name parameters.json | head -1)
  FOUND=""
  [ -n "$AERIE_DUMP" ] && FOUND="$FOUND aerie.dump"
  [ -n "$QUARTZ_DUMP" ] && FOUND="$FOUND quartz.dump"
  [ -n "$PARAMS_FILE" ] && FOUND="$FOUND parameters.json"
  echo "files=$(echo $FOUND)"

  # 3. A Postgres that exists for the length of this function. Same image as
  #    the cluster's own major version (containers/backup/Dockerfile pins it
  #    to the CNPG tag), so a restore that works here works there.
  if [ -n "$AERIE_DUMP" ] || [ -n "$QUARTZ_DUMP" ]; then
    if initdb -D "$PGDATA" -U rehearsal --auth=trust >/dev/null 2>&1 &&
       pg_ctl -D "$PGDATA" -o "-p 5433 -k $SCRATCH" -w start -l "$SCRATCH/pg.log" >/dev/null 2>&1; then

      for PAIR in "aerie:$AERIE_DUMP" "quartz:$QUARTZ_DUMP"; do
        NAME="${PAIR%%:*}"
        DUMP="${PAIR#*:}"
        [ -z "$DUMP" ] && continue

        createdb -h "$SCRATCH" -p 5433 -U rehearsal "${NAME}_rehearsal" >/dev/null 2>&1

        # --no-owner --no-privileges because the roles the dump names exist in
        # the cluster and not in this five-second instance; no --clean because
        # the database was created empty a line ago. Errors are counted rather
        # than fatal: a restore that emits warnings and still lands every
        # table is a different finding from one that lands nothing.
        RESTORE_LOG="$SCRATCH/$NAME-restore.log"
        pg_restore --no-owner --no-privileges -h "$SCRATCH" -p 5433 -U rehearsal \
          -d "${NAME}_rehearsal" "$DUMP" > "$RESTORE_LOG" 2>&1
        echo "${NAME}_restore_exit=$?"
        RESTORE_ERRORS=$(grep -c "^pg_restore: error" "$RESTORE_LOG" 2>/dev/null)
        echo "${NAME}_restore_errors=${RESTORE_ERRORS:-0}"

        # 'storage' alongside 'public' for the reason
        # deploy/cluster/data/schema/restore.sh gives: the module contexts put
        # some tables outside 'public', so a count of 'public' alone would
        # pass against a dump missing every module-owned table.
        if [ "$NAME" = "aerie" ]; then
          WHERE="table_schema IN ('public','storage')"
        else
          WHERE="table_schema NOT IN ('pg_catalog','information_schema')"
        fi
        echo "${NAME}_tables=$(psql -h "$SCRATCH" -p 5433 -U rehearsal -d "${NAME}_rehearsal" -tAc \
          "SELECT count(*) FROM information_schema.tables WHERE $WHERE" 2>/dev/null | tr -d " ")"
      done

      pg_ctl -D "$PGDATA" -o "-k $SCRATCH" stop -m fast >/dev/null 2>&1
    else
      echo "error:could not stand up the scratch Postgres: $(tail -3 "$SCRATCH/pg.log" 2>/dev/null | tr "\n" " ")"
    fi
  fi

  # 4. The parameter export (8b.7). Names only - they are not secret and the
  #    values are, and this log goes wherever kubectl logs go.
  if [ -n "$PARAMS_FILE" ]; then
    echo "param_count=$(jq 'length' "$PARAMS_FILE" 2>/dev/null)"
    jq -r '.[].Name' "$PARAMS_FILE" 2>/dev/null | while read -r NAME; do
      [ -n "$NAME" ] && echo "param:$NAME"
    done

    # The self-referential proof. `jq -j` so no newline is appended to a value
    # that does not have one; nothing but the two hashes' equality leaves this
    # block.
    GOT_HASH=$(jq -j --arg n "$PREFIX/backup/restic-password" \
      '.[] | select(.Name==$n) | .Value' "$PARAMS_FILE" 2>/dev/null | sha256sum | cut -d" " -f1)
    EMPTY_HASH=$(printf '%s' "" | sha256sum | cut -d" " -f1)
    if [ "$GOT_HASH" = "$EMPTY_HASH" ]; then
      echo "password_match=absent"
    elif [ "$GOT_HASH" = "$WANT_HASH" ]; then
      echo "password_match=yes"
    else
      echo "password_match=no"
    fi

    # restic preserves the 0600 cluster-backup.sh wrote it with, and a restore
    # that widened it would be a finding about the restore rather than about
    # the export.
    echo "param_mode=$(stat -c '%a' "$PARAMS_FILE" 2>/dev/null)"
  fi

  rm -rf "$SCRATCH"
  echo "completed=yes"
}

'@ -replace "`r`n", "`n"

    foreach ($repository in $repositoriesToRehearse) {
        $variable = if ($repository -eq 'local') { 'RESTIC_REPOSITORY_LOCAL' } else { 'RESTIC_REPOSITORY_S3' }
        $rehearsalScript += ('rehearse ''{0}'' "${{{1}:-}}"' -f $repository, $variable) + "`n"
    }
    $rehearsalScript += "echo '=== rehearsalend'`n"

    $container = @(Get-Path $template 'spec.containers')[0]
    $container.command = @('sh', '-c', $rehearsalScript)
    if ($container.PSObject.Properties['args']) { $container.args = $null }

    # backoffLimit 0: a rehearsal should not retry a repository that answered
    # once. activeDeadlineSeconds so a Job wedged on an unreachable SMB mount
    # dies on its own rather than living until someone notices it.
    $jobManifest = [pscustomobject]@{
        apiVersion = 'batch/v1'
        kind       = 'Job'
        metadata   = [pscustomobject]@{ name = $JobName; namespace = 'aerie' }
        spec       = [pscustomobject]@{
            backoffLimit            = 0
            activeDeadlineSeconds   = $JobTimeoutSeconds
            ttlSecondsAfterFinished = 1800
            template                = $template
        }
    }
    $json = $jobManifest | ConvertTo-Json -Depth 40 -Compress

    # base64 over -StdIn, then `tr -dc` in front of the decode, for the reason
    # Test-Backup.ps1's note spells out at length: -StdIn has been observed
    # welding a UTF-8 BOM onto what it carries, a BOM is fatal to JSON, and it
    # lands in front of the base64 *text* where `base64 -d` answers 'invalid
    # input' and kubectl reports the downstream symptom on a different stream.
    # The filter keeps only the base64 alphabet, so a BOM, a CR, an LF and the
    # interleaved NULs of a UTF-16 conversion are all deleted without any of
    # them having to be anticipated by name.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))

    Write-Host "Creating Job aerie/$JobName from the live aerie-backup pod template..."
    $create = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -StdIn $encoded -Command (
        'sudo k3s kubectl -n aerie delete job ' + $JobName + ' --ignore-not-found >/dev/null 2>&1; ' +
        'AERIE_JOB_FILE=$(mktemp /tmp/aerie-dr-rehearsal.XXXXXX); ' +
        'trap ''rm -f $AERIE_JOB_FILE'' EXIT INT TERM; ' +
        'tr -dc ''A-Za-z0-9+/='' | base64 -d > $AERIE_JOB_FILE 2>/dev/null || ' +
        '{ echo ''the base64 Job manifest did not decode on this node - it was truncated or mangled in transit'' >&2; exit 1; }; ' +
        'test -s $AERIE_JOB_FILE || ' +
        '{ echo ''the Job manifest never arrived on standard input'' >&2; exit 1; }; ' +
        'sudo k3s kubectl -n aerie create -f $AERIE_JOB_FILE 2>&1'
    )
    if ($create.ExitCode -ne 0) {
        throw "Could not create the rehearsal Job: $("$($create.StdOut) $($create.StdErr)".Trim())"
    }

    $minutes = [math]::Round($JobTimeoutSeconds / 60)
    Write-Host "Waiting for it - a full restore of each repository plus two pg_restores, up to $minutes min. Nothing to do but wait."
    $wait = Invoke-NodeSsh @ssh -ConnectTimeoutSec ($JobTimeoutSeconds + 120) -Command (
        'sudo k3s kubectl -n aerie wait --for=condition=complete --timeout=' + $JobTimeoutSeconds + 's job/' + $JobName + ' >/dev/null 2>&1; ' +
        'sudo k3s kubectl -n aerie logs job/' + $JobName + ' 2>&1'
    )
    $rehearsalOutput = [string]$wait.StdOut
    $ranToCompletion = $rehearsalOutput -match '=== rehearsalend'

    if ($ranToCompletion) {
        Write-Host 'Rehearsal Job completed.' -ForegroundColor Green
    }
    else {
        # Not a throw: a Job that died half way through still restored
        # whatever it restored before it died, and those sections are the
        # diagnosis. The checks below turn every unevaluated one into a
        # failure on its own line.
        Write-Host 'The rehearsal Job did not run to completion - checks below report whatever it managed before it stopped.' -ForegroundColor Yellow
    }

    if ($KeepJob) {
        Write-Host "Leaving Job aerie/$JobName in place (-KeepJob). Read it with: kubectl -n aerie logs job/$JobName"
    }
    else {
        $cleanup = Invoke-NodeSsh @ssh -ConnectTimeoutSec 60 -Command (
            'sudo k3s kubectl -n aerie delete job ' + $JobName + ' --ignore-not-found 2>&1'
        )
        if ($cleanup.ExitCode -ne 0) {
            Write-Host "Warning: could not delete Job aerie/$JobName - it has a 1800s TTL and will go on its own." -ForegroundColor Yellow
        }
    }
