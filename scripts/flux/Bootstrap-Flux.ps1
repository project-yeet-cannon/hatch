<#
.SYNOPSIS
    Installs Flux on the cluster and points it at this repository, over SSH -
    the step that turns every later change into a git commit instead of a
    kubectl apply.

.DESCRIPTION
    TODO_SWARM.md Phase 2's last step. The plan originally wrote this one as
    "run once from an operator machine", which would have made it the only
    provisioning step in the repo with no auditable, repeatable path. It
    automates the same way Provision 0-2 did, so it is scripted here and
    wrapped by provision-3-bootstrap-flux.yml. Running it by hand is the
    fallback, not the norm.

    This deliberately does *not* run `flux bootstrap github`. Bootstrap's
    convenience is that it commits Flux's own manifests back to the repo, and
    that is the one thing docs/ethos.md forbids: gotk-sync.yaml carries one
    installation's owner/repo/branch, and gotk-components.yaml becomes a second
    pin for the Flux version scripts/versions.json already owns - a 10k-line
    file that every downstream fork would re-conflict on at every re-run. The
    two halves of bootstrap are done explicitly instead:

      flux install                 -> the controllers, at the pinned version
      flux create source git       -> where to sync from
      flux create kustomization    -> what to reconcile

    Nothing is written to the repository. The sync configuration lives in the
    cluster, declared from this script's parameters, which is where a value
    that is true of exactly one installation belongs.

    The install runs *on the node* rather than from this machine: k3s already
    has a root-owned kubeconfig at /etc/rancher/k3s/k3s.yaml, so nothing has to
    copy cluster credentials onto a runner to make this work.

    Stages:
      1. Preflight  - SSH key resolves, node answers 22 and 6443, the Flux
                      version is a real pin, and the repository is reachable
                      with whatever credential (or none) was supplied.
      2. Install    - installs the pinned flux CLI on the node if it isn't
                      already at that exact version.
      3. Precheck   - `flux check --pre` against the live apiserver.
      4. Controllers- `flux install`, at the pinned version.
      5. Sync       - the GitRepository and Kustomization that point the
                      cluster at -Path on -Branch.
      6. Verify     - `flux check` plus the reconciliation state of everything
                      it now manages.

    Idempotent: `flux install` upgrades an existing installation in place and
    the two `flux create` calls upsert, so a re-run after a version bump or a
    lost node is a normal thing to do. Bumping flux.version in
    scripts/versions.json and re-dispatching is the upgrade path - there is no
    committed copy of the controllers to keep in step with it.

.PARAMETER GitHubToken
    Not a parameter, deliberately - and now usually not needed at all. A public
    repository is cloned anonymously over HTTPS, which is the expected shape
    once this repo is open source: no credential, nothing to rotate, nothing to
    register.

    For a private repository, set FLUX_GITHUB_TOKEN in the environment. It
    needs **contents:read** and nothing else (classic equivalent: `repo`, whose
    read half is what gets used). It is stored as the `flux-system` Secret in
    the cluster and reaches the node on stdin, never in argv.

    Note what is no longer required: `flux bootstrap` needed contents:*write*
    to commit its manifests and administration:write to register a deploy key,
    which together are why secrets.GITHUB_TOKEN could not be used. Neither
    applies here.

.PARAMETER Path
    What Flux reconciles, relative to the repository root. Everything under it
    becomes cluster state; that is the whole point. Unlike under `flux
    bootstrap`, Flux never writes to this path - the directory has to already
    exist on -Branch, which is why deploy/cluster/kustomization.yaml is
    committed as a skeleton.

.PARAMETER FluxVersion
    Optional override. The pin normally comes from scripts/versions.json
    ('flux.version'), committed alongside the manifests Flux reconciles so a
    re-run from an old tag installs that tag's Flux. Pass this only for a
    one-off by-hand run; a real bump is a commit to that file.

.EXAMPLE
    .\Bootstrap-Flux.ps1 -IPAddress 10.0.0.21 -GitHubOwner someone `
        -SshPrivateKeyPath ~\.ssh\id_ed25519

.EXAMPLE
    $env:FLUX_GITHUB_TOKEN = '<pat with contents:read>'
    .\Bootstrap-Flux.ps1 -IPAddress 10.0.0.21 -GitHubOwner someone `
        -SshPrivateKeyPath ~\.ssh\id_ed25519
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(\d{1,3}\.){3}\d{1,3}$')]
    [string]$IPAddress,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,38}$')]
    [string]$GitHubOwner,

    [ValidatePattern('^[A-Za-z0-9._-]{1,100}$')]
    [string]$Repository = 'Aerie',

    [ValidatePattern('^[A-Za-z0-9._/-]{1,255}$')]
    [string]$Branch = 'main',

    [ValidatePattern('^[A-Za-z0-9._/-]{1,255}$')]
    [string]$Path = 'deploy/cluster',

    # Defaults to the committed pin in scripts/versions.json - see the
    # .PARAMETER note above before passing this explicitly.
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$FluxVersion,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [int]$SyncTimeoutMinutes = 15,

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\hyperv\lib\AerieSsh.ps1')
. (Join-Path $PSScriptRoot '..\lib\AerieVersions.ps1')

# Resolved here rather than as a param default: param() has to be the first
# statement in the file, so the manifest reader isn't loaded yet at that point.
if (-not $FluxVersion) {
    $FluxVersion = Get-AerieVersion -Name 'flux.version' -Pattern '^v\d+\.\d+\.\d+$'
    $script:FluxVersionSource = 'scripts/versions.json'
}
else {
    $script:FluxVersionSource = '-FluxVersion override'
}

$script:StageNumber = 0
function Write-Stage {
    param([string]$Message)
    $script:StageNumber++
    Write-Host ''
    Write-Host "=== [$script:StageNumber] $Message ===" -ForegroundColor Cyan
}

# Every remote command needs the root-owned k3s kubeconfig, and `sudo env
# VAR=val` rather than a bare `sudo VAR=val` prefix for the reason
# Install-K3sNode.ps1 documents: most sudoers policies reset the environment
# before exec and would silently drop the assignment.
$kubeconfig = '/etc/rancher/k3s/k3s.yaml'
$fluxEnv = "sudo env KUBECONFIG=$kubeconfig"

# HTTPS rather than SSH: an anonymous clone of a public repository needs no
# credential at all, and the authenticated case is a username/password Secret
# rather than a deploy key that would have to be registered on the repository.
$repoUrl = "https://github.com/$GitHubOwner/$Repository.git"

# Structural, so not parameters: these are Flux's own bootstrap defaults, and
# they are the same for every installation. Poll the repo every minute, re-apply
# the whole tree every ten.
$sourceInterval = '1m'
$kustomizationInterval = '10m'

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    # Optional by design - see the .PARAMETER note. Absent means an anonymous
    # clone, which only works if the repository is public; that is checked
    # below rather than left to fail four stages later.
    $token = $env:FLUX_GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) { $token = $null } else { $token = $token.Trim() }

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
            $resolvedKey = Resolve-SshPrivateKeyFile -SshPrivateKey $SshPrivateKey -SshPrivateKeyPath $SshPrivateKeyPath -VMName 'aerie-flux'
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
    if (-not (Test-TcpPort -IPAddress $IPAddress -Port 6443)) {
        $failures.Add("$IPAddress isn't answering on port 6443. Run Provision 1 against this node first - there's no apiserver here to bootstrap.")
    }

    # With no token the clone is anonymous, so the repository has to be public.
    # GitHub answers 404 (not 403) for a private repo read without credentials,
    # so a definite 404 is a definite misconfiguration and worth failing on
    # here rather than as an opaque source-controller error later. Anything
    # else - a timeout, DNS, rate limiting - says nothing about visibility and
    # must not fail a run: this executes on a home runner whose egress is not
    # this script's business.
    $authDescription = 'token (contents:read), stored as the flux-system Secret'
    if (-not $token) {
        $authDescription = 'anonymous (public repository)'
        try {
            Invoke-WebRequest -Uri "https://api.github.com/repos/$GitHubOwner/$Repository" `
                -Method Head -UseBasicParsing -TimeoutSec 15 | Out-Null
        }
        catch {
            $status = $null
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
                $status = [int]$_.Exception.Response.StatusCode
            }
            if ($status -eq 404) {
                $failures.Add("GitHub reports $GitHubOwner/$Repository as non-existent to an anonymous caller, which for a repository that does exist means it is private. Either make it public or set FLUX_GITHUB_TOKEN to a PAT with contents:read - Flux has to be able to clone it without the credentials this script is holding.")
            }
            else {
                Write-Warning "Couldn't confirm $GitHubOwner/$Repository is publicly readable ($($_.Exception.Message)). Continuing - if it turns out to be private, stage 5 fails on the source not becoming Ready."
            }
        }
    }
    else {
        # The token is checked against the exact endpoint Flux clones from,
        # with the exact credential shape it will use (basic auth, username
        # `git`), because that is the only check that proves what matters. The
        # repository API would answer 200 for a fine-grained PAT that has
        # metadata but not contents:read - which clones fine right up until it
        # doesn't.
        #
        # Worth the request: without it a wrong token is not diagnosed until
        # `flux create source git` gives up waiting for the source to go Ready,
        # which is -SyncTimeoutMinutes later and reads as a Flux problem.
        $basicAuth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("git:$token"))
        try {
            Invoke-WebRequest -Uri "https://github.com/$GitHubOwner/$Repository.git/info/refs?service=git-upload-pack" `
                -Headers @{ Authorization = "Basic $basicAuth" } `
                -UseBasicParsing -TimeoutSec 15 | Out-Null
        }
        catch {
            $status = $null
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
                $status = [int]$_.Exception.Response.StatusCode
            }
            # Same discipline as the anonymous check: only a definite answer
            # from GitHub fails a run. A timeout or a DNS failure says nothing
            # about the token and must not block a home runner.
            if ($status -in 401, 403, 404) {
                $failures.Add("FLUX_GITHUB_TOKEN cannot clone $GitHubOwner/$Repository - GitHub answered HTTP $status to a git-upload-pack request. 401 means the credential was rejected outright, usually an expired, revoked or truncated PAT. 403/404 means the token is valid but carries no contents:read on this repository (for a fine-grained PAT, check it grants Contents: Read and lists this repository). Flux would be handed the same credential and fail the same way, several minutes later and less legibly.")
            }
            else {
                Write-Warning "Couldn't verify FLUX_GITHUB_TOKEN against $GitHubOwner/$Repository ($($_.Exception.Message)). Continuing - if the token is wrong, stage 5 fails on the source not becoming Ready."
            }
        }
    }

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Cluster:   $IPAddress (kubeconfig $kubeconfig)"
    Write-Host "Repo:      $repoUrl ($Branch) at $Path"
    Write-Host "Auth:      $authDescription"
    Write-Host "Flux:      $FluxVersion (pinned, from $script:FluxVersionSource)"
    if ($keyFingerprint) { Write-Host "SSH key:   $keyFingerprint" }
    Write-Host 'Preflight OK.'

    if ($PreflightOnly) {
        Write-Host ''
        Write-Host '-PreflightOnly: stopping here without touching the cluster.' -ForegroundColor Yellow
        return
    }

    $knownHostsFile = Join-Path ([IO.Path]::GetTempPath()) "aerie-flux-$([Guid]::NewGuid().ToString('N')).known_hosts"
    $ssh = @{ IPAddress = $IPAddress; User = $Username; KeyPath = $privateKeyPath; KnownHostsFile = $knownHostsFile }

    # ---------------------------------------------------------------- #
    Write-Stage 'Install the flux CLI'
    # ---------------------------------------------------------------- #

    $installed = Invoke-NodeSsh @ssh -Command "command -v flux >/dev/null 2>&1 && flux --version 2>/dev/null || echo 'flux not installed'" -ConnectTimeoutSec 20
    if ($installed.ExitCode -ne 0) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $installed.StdErr
        throw "SSH to $IPAddress as '$Username' failed$(if ($permanentReason) { ": $permanentReason" }):`n$($installed.StdErr)"
    }
    $installedVersion = $installed.StdOut.Trim()
    Write-Host "flux CLI: $installedVersion"

    # `flux version x.y.z` - the CLI prints it without the leading v.
    $wanted = "flux version $($FluxVersion.TrimStart('v'))"
    if ($installedVersion -eq $wanted) {
        Write-Host "  already at $FluxVersion - skipping install."
    }
    else {
        # Same download-then-run shape as the k3s install: leaves the script
        # on the node for post-mortem instead of piping a URL into a shell.
        $installCmd = "curl -sfL https://fluxcd.io/install.sh -o /tmp/flux-install.sh && sudo env FLUX_VERSION='$($FluxVersion.TrimStart('v'))' bash /tmp/flux-install.sh"
        Write-Host "Installing flux $FluxVersion ..."
        $install = Invoke-NodeSsh @ssh -Command $installCmd -ConnectTimeoutSec 30
        if ($install.ExitCode -ne 0) {
            throw "Installing the flux CLI on $IPAddress failed (exit $($install.ExitCode)):`n$($install.StdOut)$($install.StdErr)"
        }
        Write-Host '  installed.'
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'flux check --pre'
    # ---------------------------------------------------------------- #

    $precheck = Invoke-NodeSsh @ssh -Command "$fluxEnv flux check --pre" -ConnectTimeoutSec 30
    Write-Host ($precheck.StdOut + $precheck.StdErr).TrimEnd()
    if ($precheck.ExitCode -ne 0) {
        throw "flux check --pre failed on $IPAddress (exit $($precheck.ExitCode)). The cluster doesn't meet Flux's prerequisites - fix that before installing."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'flux install'
    # ---------------------------------------------------------------- #

    # No --components: the default set is exactly what `flux bootstrap` would
    # have installed (source, kustomize, helm and notification controllers).
    # Nothing here touches git, so this needs no credential of any kind.
    $installArgs = @(
        'install'
        "--version=$FluxVersion"
        "--timeout=${SyncTimeoutMinutes}m"
    )

    Write-Host "Installing the Flux controllers at $FluxVersion ..."
    $controllers = Invoke-NodeSsh @ssh -Command "$fluxEnv flux $($installArgs -join ' ')" -ConnectTimeoutSec 30
    $controllersOutput = ($controllers.StdOut + $controllers.StdErr).TrimEnd()
    Write-Host $controllersOutput
    if ($controllers.ExitCode -ne 0) {
        throw "flux install failed on $IPAddress (exit $($controllers.ExitCode)). The controllers never came up; nothing has been pointed at the repository yet."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Point it at the repository'
    # ---------------------------------------------------------------- #

    # What `flux bootstrap` would have written into gotk-sync.yaml and
    # committed. Declared against the cluster instead: these three values are
    # true of one installation, and docs/ethos.md keeps those out of git.
    $syncLines = New-Object Collections.Generic.List[string]
    $syncLines.Add('set -eu')

    if ($token) {
        # Single quotes only, here and below. Invoke-NodeSsh refuses a command
        # containing a double quote, because Windows PowerShell 5.1 does not
        # escape one when it builds ssh.exe's command line and the node would
        # receive this script with every " silently deleted. That is not
        # hypothetical: it is what broke this stage. `tr -d "\r\n"` arrived as
        # `tr -d rn`, which strips every r and n from the token, and GitHub
        # answered 401 on a credential that was demonstrably correct.
        #
        # Same argv discipline as Provision 2, one step further: the token goes
        # straight from standard input into a 0600 mktemp file and reaches
        # kubectl through --from-file, never passing through a shell variable
        # or an argv any local `ps` could read.
        #
        # tr is what strips the CRLF Windows PowerShell appends when it pipes a
        # string to a native command. Left in, the carriage return reaches
        # GitHub as part of the credential - the same baffling 401.
        #
        # $AERIE_TOKEN_FILE is left unquoted deliberately: mktemp's template is
        # fixed here, so the path can't contain whitespace, and a " would not
        # survive the trip anyway.
        $syncLines.Add('AERIE_TOKEN_FILE=$(mktemp /tmp/aerie-flux-token.XXXXXX)')
        $syncLines.Add('trap ''rm -f $AERIE_TOKEN_FILE'' EXIT INT TERM')
        $syncLines.Add('tr -d ''\r\n'' > $AERIE_TOKEN_FILE')
        $syncLines.Add('test -s $AERIE_TOKEN_FILE || { echo ''the token never arrived on standard input'' >&2; exit 1; }')
        # create|apply rather than create: this run may be a re-run, and
        # `kubectl create secret` on an existing Secret is a hard failure.
        $syncLines.Add('sudo k3s kubectl create secret generic flux-system -n flux-system' +
            ' --from-literal=username=git --from-file=password=$AERIE_TOKEN_FILE' +
            ' --dry-run=client -o yaml | sudo k3s kubectl apply -f -')
    }

    $sourceArgs = @(
        'create', 'source', 'git', 'flux-system'
        "--url=$repoUrl"
        "--branch=$Branch"
        "--interval=$sourceInterval"
        "--timeout=${SyncTimeoutMinutes}m"
    )
    if ($token) { $sourceArgs += '--secret-ref=flux-system' }
    $syncLines.Add("$fluxEnv flux $($sourceArgs -join ' ')")

    # --prune: an object deleted from the tree is deleted from the cluster,
    # which is what makes the repository the whole truth rather than an
    # append-only log of things that were once applied.
    $kustomizationArgs = @(
        'create', 'kustomization', 'flux-system'
        '--source=GitRepository/flux-system'
        "--path=./$($Path.Trim('/'))"
        '--prune=true'
        "--interval=$kustomizationInterval"
        "--timeout=${SyncTimeoutMinutes}m"
    )
    $syncLines.Add("$fluxEnv flux $($kustomizationArgs -join ' ')")

    # -StdIn only when there is something to send: Invoke-NodeSsh keys off
    # PSBoundParameters, so passing $null would still pipe an empty line into a
    # remote script that isn't reading one.
    $syncStdIn = @{}
    if ($token) { $syncStdIn['StdIn'] = $token }

    Write-Host "Pointing the cluster at $repoUrl ($Branch) at $Path ..."
    $sync = Invoke-NodeSsh @ssh @syncStdIn -Command ($syncLines -join "`n") -ConnectTimeoutSec 30
    $syncOutput = ($sync.StdOut + $sync.StdErr).TrimEnd()
    Write-Host $syncOutput
    if ($sync.ExitCode -ne 0) {
        $hint = if ($token) {
            'Preflight already proved this token can clone this repository, so a 401 here means the credential was damaged between here and the cluster rather than that it is wrong - check the flux-system Secret in the cluster before re-issuing the PAT.'
        }
        else {
            "Cloning anonymously - a 403/404 here means $GitHubOwner/$Repository isn't publicly readable, so set FLUX_GITHUB_TOKEN."
        }
        throw "Configuring the Flux sync failed on $IPAddress (exit $($sync.ExitCode)). $hint A 'path not found' means $Path doesn't exist on $Branch - unlike ``flux bootstrap``, nothing here creates it."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    $check = Invoke-NodeSsh @ssh -Command "$fluxEnv flux check" -ConnectTimeoutSec 30
    Write-Host ($check.StdOut + $check.StdErr).TrimEnd()
    if ($check.ExitCode -ne 0) {
        throw "flux check failed after install (exit $($check.ExitCode)) - the controllers are installed but not healthy."
    }

    $getAll = Invoke-NodeSsh @ssh -Command "$fluxEnv flux get all --all-namespaces" -ConnectTimeoutSec 30
    $getAllOutput = ($getAll.StdOut + $getAll.StdErr).TrimEnd()
    Write-Host ''
    Write-Host $getAllOutput

    # ---------------------------------------------------------------- #
    Write-Stage 'Done'
    # ---------------------------------------------------------------- #

    $elapsed = [math]::Round(((Get-Date).ToUniversalTime() - $startedUtc).TotalMinutes, 1)
    Write-Host "Flux $FluxVersion is reconciling ${GitHubOwner}/${Repository}:$Path in ${elapsed} min." -ForegroundColor Green
    Write-Host ''
    Write-Host "From here on, changes under $Path are applied by committing them - no more kubectl apply."
    Write-Host 'Nothing was committed by this run: the sync configuration lives in the cluster, not in git.'
    Write-Host 'Phase 3 starts with the External Secrets Operator HelmRelease, which consumes the'
    Write-Host 'bootstrap Secret Provision 2 planted.'
    Write-Host ''
    Write-Warning "kubectl and Flux target $IPAddress directly - there is no VIP in front of the apiserver (kube-vip fronts ingress only). Losing that node means repointing at another server by hand. Known and accepted until Phase 7; see docs/secrets-architecture.md."

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $lines = @(
            "## Flux installed on $tick$IPAddress$tick"
            ''
            '| | |'
            '|---|---|'
            "| Repository | $tick$GitHubOwner/$Repository$tick ($tick$Branch$tick) |"
            "| Path | $tick$Path$tick |"
            "| Auth | $authDescription |"
            "| Flux version | $tick$FluxVersion$tick ($script:FluxVersionSource) |"
            "| Elapsed | ${elapsed} min |"
            ''
            'Nothing was committed to the repository - the `GitRepository` and'
            '`Kustomization` live in the cluster.'
            ''
            '<details><summary>flux get all --all-namespaces</summary>'
            ''
            $fence
            $getAllOutput
            $fence
            ''
            '</details>'
        )
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }
}
finally {
    if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
    if ($knownHostsFile) { Remove-Item $knownHostsFile -Force -ErrorAction SilentlyContinue }
}
