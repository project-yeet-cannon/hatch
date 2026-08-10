# Secret seeding

Step 2 of the provisioning pipeline (TODO_SWARM.md Phase 2): pushes this
installation's secrets into the AWS SSM Parameter Store tree, then plants the
one bootstrap Secret the cluster needs to read that tree back.

The *why* — naming convention, IAM split, rotation story, what deliberately
stays out of the store — is [`docs/secrets-architecture.md`](../../docs/secrets-architecture.md).
This file is how to run it.

## Run this from the Actions tab, not by hand

**Actions → *Provision 2: Seed secrets* → Run workflow**. Same reasoning as
[`scripts/hyperv/README.md`](../hyperv/README.md#the-github-actions-workflow-is-the-way-to-run-this)
and [`scripts/k3s/README.md`](../k3s/README.md): a dispatched, auditable run is
what "seeding secrets" means here. Running
[`Sync-AerieSecrets.ps1`](Sync-AerieSecrets.ps1) directly is the fallback for
when the runner isn't reachable; the workflow is a thin wrapper around the same
script with the same parameters, so the two can't drift.

## The two files

| File | What |
|---|---|
| [`parameters.json`](parameters.json) | The map: every secret the cluster needs, its path under the prefix, and which environment variable supplies it. **No values, ever.** Committed because it's identical for every installation. |
| [`Sync-AerieSecrets.ps1`](Sync-AerieSecrets.ps1) | Reads the map, writes the values, verifies them as the ESO identity, applies the bootstrap Secret. |

Adding a secret is: add an entry to `parameters.json`, map the repository
secret onto its `env` name in the workflow, re-run. The Phase 3 `ExternalSecret`
that consumes it reads the same path.

## One-time setup

| Name | Kind | What |
|---|---|---|
| `AWS_REGION` | repository variable | Already set for `cd.yml`. The parameter tree lives in exactly one region, and the Phase 3 `ClusterSecretStore` must name the same one. |
| `SSM_AWS_ACCESS_KEY_ID` / `SSM_AWS_SECRET_ACCESS_KEY` | repository secrets | The **seed writer**: `ssm:PutParameter` on `/aerie/*` and nothing else. |
| `ESO_AWS_ACCESS_KEY_ID` / `ESO_AWS_SECRET_ACCESS_KEY` | repository secrets | The **`aerie-eso`** user: `ssm:GetParameter*` + `ssm:GetParametersByPath` on `/aerie/*`, `kms:Decrypt` on the default `aws/ssm` key. Becomes the in-cluster bootstrap Secret. |
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for Provision 0/1 — the key cloud-init baked into the node. |

Everything else the workflow passes (`AWS_ACCESS_KEY_ID`, `HA_TOKEN`,
`RESTIC_*`, …) already exists for `cd.yml`. Optional entries with no matching
secret are skipped with a note, not an error.

**Runner prerequisite:** the AWS CLI v2 must be on the runner's PATH
(`winget install --exact --id Amazon.AWSCLI`). Preflight fails with that exact
command if it's missing. Restart the runner service afterwards — a Windows
service only re-reads PATH when it starts.

### Two users, on purpose

The seed writer can write but not read; `aerie-eso` can read but not write. The
credential that sits in the cluster forever must not be able to rewrite the tree
it reads. Splitting them costs one extra IAM user and removes a whole class of
"the cluster overwrote its own secrets" failure.

## What a run actually does

1. **Preflight.** Parses the map, resolves the AWS CLI, checks every *required*
   value is present, checks both credential pairs are set, and — unless
   `stage: parameters-only` — that the node answers 22 and 6443. Nothing has
   been written at this point, so a missing secret costs a fast failure rather
   than a half-seeded tree.
2. **Seed.** For each mapped parameter: read the current value, skip if
   identical, otherwise `put-parameter` as a `SecureString`. Skipping unchanged
   values is what keeps re-runs from minting a new version per secret per run.
3. **Verify** — as the **ESO** credential, not the writer's: list the tree, then
   decrypt exactly one value. Listing alone never touches KMS, so it would
   happily pass with a missing `kms:Decrypt` and fail later as an ESO sync
   error on a resource nothing else can start without.
4. **Bootstrap.** Applies `namespace/external-secrets` and
   `secret/aerie-eso-bootstrap` to the cluster over SSH.

Idempotent end to end. Re-run it after rotating one value and only that
parameter changes.

## Stages

| `stage` | Use when |
|---|---|
| `both` | The normal run. |
| `parameters-only` | No cluster yet, or only rotating values. Needs no `ip_address`. |
| `bootstrap-only` | Rebuilding a cluster whose parameter tree is already correct. |

## Secrets never touch a command line

Values arrive as environment variables, go to AWS through
`--cli-input-json file://` (a temp file, deleted in a `finally`), and reach the
cluster over SSH **standard input** rather than in the remote command. sshd runs
the remote command through a login shell, so anything in it is visible to every
local `ps` for as long as it runs — which is why `Invoke-NodeSsh` grew a
`-StdIn` parameter rather than the obvious heredoc.

Nothing prints a value: the console and the job summary carry parameter names
and statuses only.

## Still manual

- **Creating the IAM users and their policies.** One-time AWS console/CLI work,
  same tier as generating `K3S_CLUSTER_TOKEN`. The policies are spelled out in
  [`docs/secrets-architecture.md`](../../docs/secrets-architecture.md#the-three-iam-users).
- **Rotating the underlying credentials.** Rotation is "change the value, re-run
  this" — the re-run is scripted; deciding to rotate isn't.
