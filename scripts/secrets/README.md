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

## The three files

| File | What |
|---|---|
| [`parameters.json`](parameters.json) | The map: every secret the cluster needs, its path under the prefix, which environment variable supplies it, and — from Phase 3 — which Kubernetes Secret it lands in. **No values, ever.** Committed because it's identical for every installation. |
| [`Sync-AerieSecrets.ps1`](Sync-AerieSecrets.ps1) | Reads the map, writes the values, verifies them as the ESO identity, applies the bootstrap Secret. |
| [`New-ExternalSecrets.ps1`](New-ExternalSecrets.ps1) | Reads the same map and renders the `ExternalSecret` manifests that pull the values back out. |

Adding a secret is: add an entry to `parameters.json`, map the repository
secret or variable onto its `env` name in the workflow, re-run — then give it a
`kubernetes` block and regenerate. Both halves read the one file, so the path
is written once.

## Getting a secret into the cluster

[`New-ExternalSecrets.ps1`](New-ExternalSecrets.ps1) generates
[`deploy/cluster/infrastructure/config/external-secrets/`](../../deploy/cluster/infrastructure/config/external-secrets/)
from the map — one `ExternalSecret` per target Secret, referencing the
`aerie-secrets` `ClusterSecretStore`. Those manifests are **not hand-edited**;
`ci.yml` runs the generator with `-Check` and fails the build if the tree and
the map disagree.

```powershell
pwsh ./scripts/secrets/New-ExternalSecrets.ps1           # write
pwsh ./scripts/secrets/New-ExternalSecrets.ps1 -Check    # what CI runs
```

A parameter reaches the cluster by carrying a `kubernetes` block:

```json
"kubernetes": {
  "namespace": "cert-manager",
  "secretName": "route53-credentials",
  "secretKey": "access-key-id",
  "consumedBy": "3b.10 ClusterIssuer, accessKeyIDSecretRef"
}
```

Two parameters naming the same `namespace` + `secretName` become **one**
`ExternalSecret` with two keys — which is how an access key id and its secret
half stay a single object that can't half-rotate.

The generator refuses two things, and both are the failure it exists to
prevent:

- a `kubernetes` block on a `required: false` parameter. Nothing seeds an
  optional value this installation didn't supply, and an `ExternalSecret`
  pointing at an absent parameter sits in `SecretSyncError` forever — which
  `infra-config`'s `wait: true` turns into a NotReady Kustomization and a phase
  gate nobody can pass.
- a `required: true` parameter with neither a `kubernetes` block nor a
  `kubernetesDeferred` note. That's the direction that rots quietly: a value
  seeded on every run and read by nothing looks, from every angle, like a value
  that works. `backup/*` carries such a note — its CronJob and namespace arrive
  in Phase 8.

It also rejects a namespace, Secret name or key Kubernetes would reject, two
parameters claiming one key inside one Secret, and a `$` anywhere in the
rendered output (Flux's `postBuild` would expand it, and an undefined token
expands to an empty string rather than an error).

## One-time setup

| Name | Kind | What |
|---|---|---|
| `AWS_REGION` | repository variable | Already set for `cd.yml`. The parameter tree lives in exactly one region, and the Phase 3 `ClusterSecretStore` must name the same one. |
| `SSM_AWS_ACCESS_KEY_ID` | repository **variable** | The **seed writer**'s id. |
| `SSM_AWS_SECRET_ACCESS_KEY` | repository **secret** | Its secret half. The seed writer holds `ssm:PutParameter` + `ssm:GetParameter*` on `/aerie/*` and `kms:Decrypt` through SSM. The read half is what read-before-write needs; `PutParameter` alone fails on the first parameter that already exists. |
| `ESO_AWS_ACCESS_KEY_ID` | repository **variable** | The **`aerie-eso`** user's id. |
| `ESO_AWS_SECRET_ACCESS_KEY` | repository **secret** | Its secret half. `aerie-eso` holds `ssm:GetParameter*` + `ssm:GetParametersByPath` on `/aerie` **and** `/aerie/*`, and `kms:Decrypt` through SSM. Both ARNs — see [the policies](#the-iam-policies-are-committed-not-retyped). This pair becomes the in-cluster bootstrap Secret. |
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for Provision 0/1 — the key cloud-init baked into the node. |
| `VM_LOG_SHIPPER_TOKEN` | repository secret | Required, and the one value here with **no issuer to fetch it from** — see below. |

Everything else the workflow passes (`AWS_ACCESS_KEY_ID`, `HA_TOKEN`,
`RESTIC_*`, …) already exists for `cd.yml`. Optional entries with no matching
value are skipped with a note, not an error — but `required: true` entries in
[`parameters.json`](parameters.json) fail preflight, before anything is
written.

### Secrets with no issuer

Almost everything in the tree comes from somewhere: `HA_TOKEN` from Home
Assistant, the AWS pairs from IAM, `NODE_SSH_PRIVATE_KEY` from `ssh-keygen`.
`VM_LOG_SHIPPER_TOKEN` has no issuer at all — it's a shared secret two sides
compare, so you mint it:

```bash
./scripts/secrets/new-shared-secret.sh          # print a 256-bit hex token
./scripts/secrets/new-shared-secret.sh --set    # or write it straight to the repo (needs gh)
```

Hex, not base64, because the value ends up as an HTTP header, a Windows
Scheduled Task argument, and a compose environment variable, and hex is the
one encoding none of the three quote or wrap.

**Three consumers must hold the same value**, so rotating it is three re-runs,
not one:

| Consumer | Gets it from | After a change |
|---|---|---|
| `Aerie.Api` (`VmConsoleLogsController`) | `cd.yml` → `compose.prod.yml` | re-run the deploy |
| The per-VM console-log Scheduled Task on each Hyper-V host | `provision-0-new-node.yml` | applied on that VM's next build / `recreate_vm` |
| `/aerie/logging/vm-log-shipper-token` in SSM, for Phase 3 | this workflow | re-run Provision 2 |

Until the API and the shippers agree, every shipper POST is a `401` and the
console lines are dropped — the shipper's local copy on the host keeps them.

### Which tab: ids are variables, everything else is a secret

An AWS access key **id** names an identity; it does not authenticate one. So
every `*_ACCESS_KEY_ID` here is a repository **variable** and every
`*_SECRET_ACCESS_KEY`, token and password is a repository **secret**. Each
credential pair therefore straddles both tabs of *Settings → Secrets and
variables → Actions*.

The payoff is diagnostic: an id kept out of the secret store stays unmasked in
run logs, so an `AccessDenied` shows *which* IAM user hit it instead of `***`.
[`parameters.json`](parameters.json) records the split per value as
`githubKind`, and the two workflows read the matching side.

Getting it wrong is silent — `${{ secrets.X }}` for a value stored as a
variable resolves to an empty string, not an error — so both workflows fail
fast on an empty credential and name the tab the value belongs on.

**Runner prerequisite: none to install by hand.** This workflow's *Ensure
runner dependencies* step installs the AWS CLI v2 (pinned in
[`scripts/versions.json`](../versions.json), SHA256-verified) and the OpenSSH
client before the seed runs — see [`scripts/runner/`](../runner/README.md).
That step is also what makes a same-run install usable: the MSI writes the
machine PATH, which the runner service can't see until it restarts, so the
step publishes the directory to `$GITHUB_PATH` and preflight resolves
`aws.exe` from its install directory as well as from PATH.

### Two users, on purpose

Both can read; only the seed writer can write. The asymmetry runs one way on
purpose — the credential that sits in the cluster forever must not be able to
rewrite the tree it reads, because that tree holds the restic password and S3
keys and a rewritten recovery path fails silently. The writer's extra rights
are bounded by its lifetime instead: it never leaves GitHub Actions, and only
holds a session for the length of a run. Splitting them costs one extra IAM
user and removes a whole class of "the cluster overwrote its own secrets"
failure.

### The IAM policies are committed, not retyped

[`iam/aerie-eso.policy.json`](iam/aerie-eso.policy.json) and
[`iam/seed-writer.policy.json`](iam/seed-writer.policy.json) hold the two
policies with `<AWS_REGION>`, `<AWS_ACCOUNT_ID>` and `<PARAMETER_PREFIX>` left
as placeholders, so nothing installation-specific is committed.
[`Set-AerieSecretsIam.ps1`](Set-AerieSecretsIam.ps1) fills them in from
`sts get-caller-identity` and `parameters.json`, then attaches them:

```powershell
# As an operator identity that can write IAM — not the seed writer's
.\Set-AerieSecretsIam.ps1 -SeedWriterUserName aerie-ssm-seed

# Or print the rendered JSON to paste into the console, changing nothing.
# With both values given it needs no AWS CLI and no credentials, so this
# works from a laptop that has nothing but PowerShell.
.\Set-AerieSecretsIam.ps1 -Render -AwsAccountId 123456789012 -AwsRegion us-east-1
```

It attaches policies to users that already exist; it does not create users or
access keys, because minting a key means printing one.

The reason this is a file and not a paragraph is one easy near-miss. The ESO
user needs `GetParametersByPath` on **both** `arn:…:parameter/aerie` and
`arn:…:parameter/aerie/*`. Listing authorizes against the *path* — the bare ARN
with no trailing slash — which `/aerie/*` does not match, so the natural
"scope it to `/aerie/*`" policy denies the verify stage with
`is not authorized to perform: ssm:GetParametersByPath`. Full reasoning in
[`docs/secrets-architecture.md`](../../docs/secrets-architecture.md#the-bare-path-arn-is-not-optional).

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

- **Creating the IAM users themselves, and their access keys.** One-time AWS
  console work, same tier as generating `K3S_CLUSTER_TOKEN`. Their *policies*
  are no longer manual — [`Set-AerieSecretsIam.ps1`](Set-AerieSecretsIam.ps1)
  applies the committed documents, for the reason
  [above](#the-iam-policies-are-committed-not-retyped).
- **Rotating the underlying credentials.** Rotation is "change the value, re-run
  this" — the re-run is scripted; deciding to rotate isn't.
