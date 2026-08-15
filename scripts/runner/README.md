# Runner dependencies

The tooling every Aerie provisioning workflow expects to find on the
self-hosted Windows runner, installed by the pipeline itself rather than by
hand.

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

Each workflow names only what its own script uses, so a k3s install doesn't
fail on a host that has no reason to hold AWS credentials.

Idempotent and quick when there's nothing to do: a present, current tool is
resolved and reported, never reinstalled.

## What it installs

| Dependency | What | Pinned? |
|---|---|---|
| `AwsCli` | AWS CLI v2, from the version-specific MSI. Needed by [`Sync-AerieSecrets.ps1`](../secrets/Sync-AerieSecrets.ps1) to write the SSM parameter tree. | Yes — `awsCli` in [`scripts/versions.json`](../versions.json), URL *and* SHA256, verified before the MSI runs. |
| `OpenSshClient` | The `OpenSSH.Client` Windows optional feature (`ssh.exe`, `ssh-keygen.exe`). Every provisioning script reaches the nodes over SSH. | No — it's an OS feature, so the build is whatever the host's Windows ships. There is nothing to download and nothing honest to pin. |

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
