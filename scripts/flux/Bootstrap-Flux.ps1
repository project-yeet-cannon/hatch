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

    The bootstrap runs *on the node* rather than from this machine: k3s
    already has a root-owned kubeconfig at /etc/rancher/k3s/k3s.yaml, so
    nothing has to copy cluster credentials onto a runner to make this work.

    Stages:
      1. Preflight - SSH key resolves, node answers 22 and 6443, the PAT is
                     present, and the Flux version is a real pin.
      2. Install   - installs the pinned flux CLI on the node if it isn't
                     already at that exact version.
      3. Precheck  - `flux check --pre` against the live apiserver.
      4. Bootstrap - `flux bootstrap github`, which installs the controllers,
                     commits the flux-system manifests to -Path on -Branch,
                     and registers a deploy key on the repository.
      5. Verify    - `flux check` plus the reconciliation state of everything
                     it now manages.

    Idempotent: `flux bootstrap` converges an existing installation, so a
    re-run after a version bump or a lost node is a normal thing to do.

.PARAMETER GitHubToken
    Not a parameter, deliberately - the PAT is read from the FLUX_GITHUB_TOKEN
    environment variable so it stays out of the command line and shell
    history. It needs contents:write (to commit the manifests) and
    administration:write (to register the deploy key Flux authenticates with
    from then on), plus workflows:write if -Path ever holds workflow files.
    Classic equivalent: `repo`.

.PARAMETER Path
    Where Flux's own manifests are committed and what it reconciles from.
    Everything under it becomes cluster state; that is the whole point.

.PARAMETER FluxVersion
    Optional override. The pin normally comes from scripts/versions.json
    ('flux.version'), committed alongside the manifests Flux reconciles so a
    re-bootstrap from an old tag installs that tag's Flux. Pass this only for
    a one-off by-hand run; a real bump is a commit to that file.

.PARAMETER Personal
    Set for a user-owned repository, omit for an organization-owned one -
    this is `flux bootstrap github --personal`.

.EXAMPLE
    $env:FLUX_GITHUB_TOKEN = '<pat>'
    .\Bootstrap-Flux.ps1 -IPAddress 10.0.0.21 -GitHubOwner someone `
        -Personal -SshPrivateKeyPath ~\.ssh\id_ed25519
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

    [switch]$Personal,

    [string]$Username = 'aerie',

    [string]$SshPrivateKeyPath,
    [string]$SshPrivateKey,

    [int]$BootstrapTimeoutMinutes = 15,

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

$tempKeyFile = $null
$knownHostsFile = $null
$startedUtc = (Get-Date).ToUniversalTime()

try {
    # ---------------------------------------------------------------- #
    Write-Stage 'Preflight'
    # ---------------------------------------------------------------- #

    $failures = New-Object Collections.Generic.List[string]

    $token = $env:FLUX_GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $failures.Add('FLUX_GITHUB_TOKEN is not set. Flux needs a PAT with contents:write and administration:write on the repository (classic: repo) to commit its manifests and register its deploy key.')
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

    if ($failures.Count -gt 0) {
        $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
        throw "Preflight failed with $($failures.Count) problem(s):`n$detail"
    }

    Write-Host "Cluster:   $IPAddress (kubeconfig $kubeconfig)"
    Write-Host "Repo:      $GitHubOwner/$Repository ($Branch) at $Path$(if ($Personal) { ' [personal]' } else { ' [organization]' })"
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

    $installed = Invoke-NodeSsh @ssh -Command 'command -v flux >/dev/null 2>&1 && flux --version 2>/dev/null || echo "flux not installed"' -ConnectTimeoutSec 20
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
        throw "flux check --pre failed on $IPAddress (exit $($precheck.ExitCode)). The cluster doesn't meet Flux's prerequisites - fix that before bootstrapping."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'flux bootstrap github'
    # ---------------------------------------------------------------- #

    $bootstrapArgs = @(
        'bootstrap', 'github'
        "--owner=$GitHubOwner"
        "--repository=$Repository"
        "--branch=$Branch"
        "--path=$Path"
        "--timeout=${BootstrapTimeoutMinutes}m"
    )
    if ($Personal) { $bootstrapArgs += '--personal' }

    # The PAT arrives on standard input and is read into a shell variable,
    # never into argv. `sudo env GITHUB_TOKEN=...` - and equally a
    # `$(cat tokenfile)` substitution, which the shell expands before exec -
    # would put the token in the argv of a process that lives for the whole
    # multi-minute bootstrap, readable by every local `ps`. `export` is a
    # builtin, so the value only ever reaches flux's environment.
    #
    # That in turn means not running flux under sudo: sudo scrubs the
    # environment. Instead the root-owned kubeconfig is copied to a
    # user-owned temp file (mktemp creates it 0600) that the trap removes on
    # any exit path.
    $bootstrapCmd = @(
        'set -eu'
        'IFS= read -r AERIE_GITHUB_TOKEN'
        # Windows PowerShell terminates a piped string with CRLF, so the token
        # arrives with a trailing carriage return that `read` keeps. Left in,
        # it reaches GitHub as part of the credential and comes back as a
        # baffling 401 on a token that is demonstrably correct.
        'AERIE_GITHUB_TOKEN=$(printf %s "$AERIE_GITHUB_TOKEN" | tr -d "\r\n")'
        'export GITHUB_TOKEN="$AERIE_GITHUB_TOKEN"'
        'unset AERIE_GITHUB_TOKEN'
        'AERIE_KUBECONFIG=$(mktemp /tmp/aerie-flux-kubeconfig.XXXXXX)'
        'trap ''rm -f "$AERIE_KUBECONFIG"'' EXIT INT TERM'
        "sudo cat $kubeconfig > `"`$AERIE_KUBECONFIG`""
        'export KUBECONFIG="$AERIE_KUBECONFIG"'
        "flux $($bootstrapArgs -join ' ')"
    ) -join "`n"

    Write-Host "Bootstrapping against $GitHubOwner/$Repository at $Path ..."
    $bootstrap = Invoke-NodeSsh @ssh -Command $bootstrapCmd -StdIn $token -ConnectTimeoutSec 30
    $bootstrapOutput = ($bootstrap.StdOut + $bootstrap.StdErr).TrimEnd()
    Write-Host $bootstrapOutput
    if ($bootstrap.ExitCode -ne 0) {
        throw "flux bootstrap failed on $IPAddress (exit $($bootstrap.ExitCode)). A 403 here is almost always a PAT missing administration:write, which is what registering the deploy key needs."
    }

    # ---------------------------------------------------------------- #
    Write-Stage 'Verify'
    # ---------------------------------------------------------------- #

    $check = Invoke-NodeSsh @ssh -Command "$fluxEnv flux check" -ConnectTimeoutSec 30
    Write-Host ($check.StdOut + $check.StdErr).TrimEnd()
    if ($check.ExitCode -ne 0) {
        throw "flux check failed after bootstrap (exit $($check.ExitCode)) - the controllers are installed but not healthy."
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
    Write-Host 'Phase 3 starts with the External Secrets Operator HelmRelease, which consumes the'
    Write-Host 'bootstrap Secret Provision 2 planted.'
    Write-Host ''
    Write-Warning "kubectl and Flux target $IPAddress directly - there is no VIP in front of the apiserver (kube-vip fronts ingress only). Losing that node means repointing at another server by hand. Known and accepted until Phase 7; see docs/secrets-architecture.md."

    if ($env:GITHUB_STEP_SUMMARY) {
        $tick = [char]0x60
        $fence = "$tick$tick$tick"
        $lines = @(
            "## Flux bootstrapped on $tick$IPAddress$tick"
            ''
            '| | |'
            '|---|---|'
            "| Repository | $tick$GitHubOwner/$Repository$tick ($tick$Branch$tick) |"
            "| Path | $tick$Path$tick |"
            "| Flux version | $tick$FluxVersion$tick ($script:FluxVersionSource) |"
            "| Elapsed | ${elapsed} min |"
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
