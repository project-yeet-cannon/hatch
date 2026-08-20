# Runner dependencies

The tooling every Aerie workflow expects to find on the self-hosted Windows
runner, installed by the pipeline itself rather than by hand.

Two groups, for two reasons. The provisioning workflows have always needed the
AWS CLI and an SSH client here. The build toolchain — pwsh, kubectl, helm, jq,
gh, the Android SDK — is newer: `ci.yml` and `publish.yml` used to run on
GitHub's `ubuntu-latest`, which came with all of it preinstalled, and they now
run on the `builder` pool instead. Nothing on a Windows runner is
preinstalled, so it is installed here.

| File | What |
|---|---|
| [`Install-RunnerDependencies.ps1`](Install-RunnerDependencies.ps1) | Entry point. Ensures the named dependencies, or all of them. |
| [`lib/AerieRunnerDependencies.ps1`](lib/AerieRunnerDependencies.ps1) | The shared functions — resolve, and install. Dot-sourced by the entry point *and* by the provisioning scripts that need to find a tool without installing it. |

## Why this exists

A prerequisite that lives only in a README is wrong on the next machine. Every
provisioning workflow now runs this as its first step after checkout, so a
runner converges on what it needs by being used:

```yaml
      - name: Ensure runner dependencies
        shell: powershell
        run: |
          $ErrorActionPreference = 'Stop'
          Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
          & .\scripts\runner\Install-RunnerDependencies.ps1 -Dependency AwsCli, OpenSshClient
```

Each workflow names only what its own jobs use, so a k3s install doesn't fail
on a host that has no reason to hold AWS credentials, and a container build
doesn't drag in an Android SDK.

Note `shell: powershell`. `ci.yml` and `publish.yml` set a workflow-level
`defaults.run.shell: bash`, because a Windows runner defaults to PowerShell and
every other `run:` block in them is bash. This step overrides it back: it is
the step that installs the toolchain, so it is the one step that can't assume
one.

Two things a job doing this must also get right:

- **`sparse-checkout` must include `scripts`.** The entry point dot-sources
  `scripts/lib/AerieVersions.ps1` and reads `scripts/versions.json`; a sparse
  checkout that omits them fails with a missing-file error that reads like a
  bug in this script.
- **Order matters for `AndroidSdk`** — after `actions/setup-java`, per above.

Idempotent and quick when there's nothing to do: a present, current tool is
resolved and reported, never reinstalled.

## What it installs

| Dependency | What | Pinned? |
|---|---|---|
| `AwsCli` | AWS CLI v2, from the version-specific MSI. Needed by [`Sync-AerieSecrets.ps1`](../secrets/Sync-AerieSecrets.ps1) to write the SSM parameter tree. | Yes — `awsCli` in [`scripts/versions.json`](../versions.json), URL *and* SHA256, verified before the MSI runs. |
| `OpenSshClient` | The `OpenSSH.Client` Windows optional feature (`ssh.exe`, `ssh-keygen.exe`). Every provisioning script reaches the nodes over SSH. | No — it's an OS feature, so the build is whatever the host's Windows ships. There is nothing to download and nothing honest to pin. |
| `PowerShell7` | `pwsh`, from the version-specific MSI. `ci.yml` runs [`New-ExternalSecrets.ps1`](../secrets/New-ExternalSecrets.ps1) with it. Installs *beside* Windows PowerShell 5.1, which the provisioning scripts still target. | Yes — `powerShell`, URL and SHA256 (Microsoft's own, from the release's `hashes.sha256`). |
| `Kubectl` | `kubectl.exe`, which carries kustomize. `ci.yml` builds every kustomization under `deploy/` with it. | Yes — `kubectl`, URL and SHA256 (Kubernetes' own). Held to the same minor as the `k3s` pin; bump the two together. |
| `Helm` | `helm.exe`. `ci.yml` lints and renders `charts/aerie`. | Yes — `helm`, URL and SHA256 (Helm's own). Held on the v3 line deliberately — see the note in `versions.json`. |
| `Jq` | `jq.exe`. Used by `ci.yml`'s substitution-token check and by both composite actions under `.github/actions/`. | Yes — `jq`, URL and SHA256 (jq's own). |
| `GitHubCli` | `gh.exe`. [`wait-for-ci`](../../.github/actions/wait-for-ci) polls the Actions API with it, which is the gate every image push waits on. | Yes — `githubCli`, URL and SHA256 (the cli project's own). |
| `AndroidSdk` | Google's command-line tools, then `platform-tools`, `platforms;android-34` and `build-tools;34.0.0` fetched by `sdkmanager`. Builds the kiosk APK. | The bootstrap zip, yes — `androidSdk`, URL and SHA256. The packages `sdkmanager` then downloads are verified by `sdkmanager` against Google's own manifest, which is the only integrity story on offer for them. |
| `Docker` | **Checked, never installed.** Verifies `docker.exe` exists, the daemon answers, and it is in *Linux*-container mode. | n/a — see below. |

`AndroidSdk` is the one dependency left out of the default set: it is ~700MB,
and only the two kiosk jobs want it. Ask for it by name. It also has an
ordering requirement the others don't — `sdkmanager` is a Java program, so
`actions/setup-java` has to run *before* it.

## Why Docker isn't installed

Every `publish.yml` job builds a Linux image, so those runners need a Docker
daemon — but a Linux-container daemon on a Windows host is Docker Desktop with
a WSL2 backend, or a daemon in a VM. Neither is a hash-pinned MSI that can be
dropped on a machine unattended, and a half-working silent install of the thing
every image build depends on is worse than a clear message saying it's missing.

So it stays an operator-installed prerequisite, and the `Docker` dependency
reports on it instead. It separates three failures that have three different
fixes:

- `docker.exe` missing entirely.
- Present, but the daemon isn't answering. **This is the one to expect after a
  reboot**: Docker Desktop is not a Windows service, and it does not start on
  its own with nobody logged in.
- Answering, but in Windows-container mode — everything looks installed and
  every build fails on the base image.

## Where installed tools go

Under `%ProgramData%\Aerie\runner-tools\<tool>\<version>\`, not the runner's
work directory — that gets wiped between jobs, which would mean re-downloading
the Android SDK on every push. Version-stamped, so bumping a pin installs
alongside the old copy instead of fighting it, and an unchanged pin costs one
`Test-Path`.

A tool already on `PATH` at or above its pin is left alone rather than
replaced — the same floor-not-exact-match rule the AWS CLI has always used.

The AWS CLI pin is a **floor, not an exact match**: a runner already carrying a
newer `aws.exe` keeps it, with a warning. These runners are shared with
`cd.yml` and anything else the operator dispatches, so downgrading a CLI
another job depends on would be a worse failure than running one release
ahead. Missing or older installs are brought up to the pinned version exactly.

## The PATH problem it solves

The MSI adds itself to the **machine** PATH. A Windows service only re-reads
its environment when it starts, so the runner service — and every job process
it spawns — can't see that entry until the service is restarted. Installing
and using a tool in the same run would be impossible.

Two things close that gap, both in
[`lib/AerieRunnerDependencies.ps1`](lib/AerieRunnerDependencies.ps1):

- `Get-AerieAwsCliPath` searches the MSI's install directory as well as PATH,
  so a resolve succeeds regardless.
- `Publish-AerieToolPath` prepends the directory to `$env:PATH` (this process)
  and appends it to `$env:GITHUB_PATH` (every later step of the same job).

Neither writes to the machine PATH — the installer already did, for every
process started after the next service restart.

## Running it by hand

The fallback, same as everywhere else in `scripts/`:

```powershell
# From an elevated PowerShell, in a full checkout
.\scripts\runner\Install-RunnerDependencies.ps1

# Audit without changing anything (works unelevated)
.\scripts\runner\Install-RunnerDependencies.ps1 -CheckOnly
```

Installing needs an elevated session. Dispatched runs already have one: the
runner service runs as a local Administrator, which
[`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml)
requires anyway for the Hyper-V cmdlets.

## What stays manual

Registering the runner itself. That needs a registration token only a human
can mint, so it's the one step the pipeline can't bootstrap — see the runner
requirements in
[`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml).
