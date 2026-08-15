# Secrets Architecture

## Summary

How a secret gets from an operator's hands into a running pod, without any
secret bytes ever entering this repository.

Git holds **pointers** — a parameter path, an `ExternalSecret` reference, a
variable name. Those are structural: identical for every installation, which is
what makes them committable under [`docs/ethos.md`](ethos.md). The bytes behind
them live in an external secret store, chosen per install.

```text
operator (printed / offline copy)
  └→ GitHub Actions repository secrets
       └→ Provision 2 ──put──→ AWS SSM Parameter Store  /aerie/*   (SecureString)
       └→ Provision 2 ──ssh──→ bootstrap Secret in-cluster (never in git)
                                    └→ ESO ClusterSecretStore
                                         └→ ExternalSecret → real k8s Secret → pod
```

Exactly **one** imperative secret injection per installation: the bootstrap
credential that lets the cluster reach its own store. Everything downstream of
it is a committed manifest.

## Why not SOPS

The original cluster plan called for SOPS + age with encrypted secrets
committed to the repo. That was rejected — not on cryptographic grounds, which
are sound, but because Aerie is a product that produces deployments. A committed
encrypted secret is one operator's secret sitting in a shared artifact:
meaningless to everyone downstream, and `.sops.yaml` creation rules are
architecture that only makes sense if the repo has exactly one owner. See
[`docs/ethos.md`](ethos.md#no-keys-in-the-repo).

The practical win is rotation. With ESO, rotating a secret is: change the
parameter, wait for `refreshInterval`, let reloader bounce the pods. **No
commit, no deploy.** A SOPS rotation *is* a commit.

## The store is a parameter, not a decision

ESO speaks to many backends. The *interface* — `ExternalSecret` manifests and
the paths below — is committed; the *provider* is chosen per install.

This installation uses **AWS SSM Parameter Store**: the AWS account already
exists for Route53 DNS-01 and the restic S3 bucket, `SecureString` parameters
are free at this scale (Secrets Manager would be $0.40/secret/month), and it
puts the keys off-site, which is a better DR story than a key that only lives on
the nodes it protects.

**A self-hosted provider — in-cluster OpenBao — is required before
open-sourcing.** Forcing every home user to open an AWS account to run a home
server defeats the premise. The `ClusterSecretStore` is deliberately kept in its
own manifest so that swap touches one file.

## Naming convention

```text
/<prefix>/<component>/<key>
```

| Segment | Rule |
|---|---|
| `prefix` | `/aerie` by default. The one part that is an operator value: two installations sharing an AWS account give each its own, via `-ParameterPrefix` / the workflow's `parameter_prefix` input. The ESO IAM policy and the `ClusterSecretStore` must agree with whatever is chosen. |
| `component` | The **consumer** in the cluster — a Helm release or operator (`cert-manager`, `postgres`, `ha`, `backup`, `logging`, `kiosk`). Not the producer: scoping by consumer means the blast radius of a wrong value is legible from the path alone. |
| `key` | What the credential *is* (`route53-access-key-id`, `token`, `restic-password`), not whose it is. Lowercase kebab-case, no environment segment — one cluster per prefix. |

Everything is stored as `SecureString` with the default `aws/ssm` KMS key. Even
the values that look unremarkable: a mixed tree invites a future reader to guess
which kind they're holding.

### The tree

The authoritative list is [`scripts/secrets/parameters.json`](../scripts/secrets/parameters.json)
— committed, no values in it — which both the seeding script and (from Phase 3)
the `ExternalSecret` manifests read. Change a path there and nowhere else.

| Parameter | Consumer | Phase |
|---|---|---|
| `/aerie/cert-manager/route53-access-key-id` | cert-manager DNS-01 | 3 |
| `/aerie/cert-manager/route53-secret-access-key` | cert-manager DNS-01 | 3 |
| `/aerie/ha/token` | Aerie.Api → Home Assistant | 3 |
| `/aerie/logging/vm-log-shipper-token` | node console-log shipper → API | 3 |
| `/aerie/postgres/wal-s3-access-key-id` | CloudNativePG WAL archiving | 4 |
| `/aerie/postgres/wal-s3-secret-access-key` | CloudNativePG WAL archiving | 4 |
| `/aerie/backup/restic-password` | restic CronJob | 8 |
| `/aerie/backup/s3-access-key-id` | restic CronJob | 8 |
| `/aerie/backup/s3-secret-access-key` | restic CronJob | 8 |
| `/aerie/kiosk/wifi-password` | kiosk provisioning QR | 3 |
| `/aerie/tailscale/auth-key` | subnet router, if it follows the stack in | 5 |

Entries ahead of their phase are listed so the convention is settled before the
manifest that needs it exists. Seeding skips any whose value isn't supplied.

### What deliberately stays out

- **`K3S_CLUSTER_TOKEN`, `NODE_SSH_PRIVATE_KEY`** — needed to *build* the
  cluster that would hold them. Chicken-and-egg; they stay GitHub Actions
  secrets.
- **`RESTIC_PASSWORD`'s printed offline copy** — the parameter is a
  convenience for the Phase 8 CronJob. The printed copy remains the root of
  trust: a backup password that only exists in the account you're trying to
  recover from is not a recovery plan. Phase 8 closes the loop from the other
  direction by exporting the `/aerie/*` tree *into* the restic repos.
- **The ESO bootstrap credential** — it is the key to the store, so it cannot
  live in the store.

## The three IAM users

Same isolation discipline as Phase 0's `aerie-restic`: one user per job, each
scoped to exactly what it needs.

| User | Rights | Held by |
|---|---|---|
| seed writer | `ssm:PutParameter`, `ssm:GetParameter` on `/aerie/*`, `kms:Decrypt` on `aws/ssm` | `SSM_AWS_*` repository secrets, used only by Provision 2 |
| `aerie-eso` | `ssm:GetParameter*`, `ssm:GetParametersByPath` on `/aerie/*`, `kms:Decrypt` on `aws/ssm` | the in-cluster bootstrap Secret |
| Route53 / `aerie-restic` | unchanged from Phase 0 | seeded *as values* into the tree above |

The split is one-directional, not disjoint: the writer reads *and* writes, the
ESO user only reads. The writer needs the read half for read-before-write below
— a `PutParameter`-only policy fails on the first parameter that already
exists.

What the split buys is the other direction. The credential the cluster holds
forever must not be able to write the tree it reads: it sits in etcd for the
life of the install, so anything that can read that Secret — the ESO pod, a
`kubectl get secret -n external-secrets`, an etcd snapshot in a backup — would
otherwise be able to overwrite every credential above it. Including the restic
password and S3 keys, which makes the worst case a silently rewritten recovery
path rather than a leak. That is why the first two are separate users, and it
costs one IAM user to close.

The writer's extra rights are bounded by its lifetime instead: it exists only
in GitHub Actions and only holds an AWS session for the length of a Provision 2
run.

## Seeding: Provision 2

**Actions → *Provision 2: Seed secrets*** runs
[`scripts/secrets/Sync-AerieSecrets.ps1`](../scripts/secrets/Sync-AerieSecrets.ps1).
Full detail in [`scripts/secrets/README.md`](../scripts/secrets/README.md). The
parts worth knowing here:

- **Read before write.** A parameter whose value hasn't changed is skipped, not
  rewritten. SSM keeps 100 versions; a workflow that wrote unconditionally
  would burn them and destroy the version history's value as an audit of when a
  secret actually rotated.
- **Values never reach a command line.** They arrive as environment variables,
  go to AWS via `--cli-input-json file://`, and reach the cluster over SSH
  standard input. Arguments are readable by any local `ps`.
- **Verification runs as the ESO identity**, not the writer's — listing *and* a
  single decrypt, because `GetParametersByPath` without decryption never
  touches KMS and would hide a missing `kms:Decrypt` until ESO failed with it
  in production.
- **Nothing prints a secret value**, including the job summary, which lists
  parameter names and statuses only.

## Rotation

1. Update the GitHub Actions repository secret (or whatever future source
   replaces it).
2. Re-run **Provision 2**. Only the changed parameter gets a new version.
3. ESO re-syncs on its `refreshInterval`; reloader restarts the affected pods.

No commit, no deploy, no cluster access needed. That is the property this whole
design exists to buy.

## Known gap: no VIP in front of the apiserver

`kubectl`, Provision 2 and Provision 3 all target **one node's IP directly**.
kube-vip (Phase 3) fronts *ingress* traffic; it does not front the apiserver.

Consequences, stated so they're a decision and not a surprise:

- Losing that node means repointing at another server by hand — re-run the
  workflow with a different `ip_address`, and edit any local kubeconfig's
  `server:` field.
- Nothing is lost by it: the cluster state is in etcd on every server, and both
  workflows are idempotent, so pointing them at a survivor is the whole
  recovery.

Acceptable for a home cluster, and cheap to revisit: an apiserver VIP is
another kube-vip instance, or an haproxy in front of `:6443`. Revisit if the
manual repoint ever costs more than that. Tracked in
[`TODO_SWARM.md`](../TODO_SWARM.md) Phase 2.

## Related

- [`docs/ethos.md`](ethos.md) — why no secret bytes are committed, encrypted or
  otherwise
- [`docs/disaster-recovery.md`](disaster-recovery.md) — the restic side of the
  same trust chain
- [`scripts/secrets/README.md`](../scripts/secrets/README.md) — running the seed
- [`scripts/flux/README.md`](../scripts/flux/README.md) — the bootstrap that
  starts consuming these
