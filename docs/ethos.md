# Aerie Ethos

## Summary

Aerie is not a deployment. It is a **product that produces deployments**.

The long-term goal is to open-source this repo and ship it as something a person
can run at home — eventually turnkey, an installer that bootstraps an enterprise-
grade home cloud on their own hardware, under their own domain, with their own
data. That goal is not a future rewrite. It is a constraint on every commit made
today, because a repo that accumulates operator-specific facts cannot be handed
to a second operator without a painful excavation later.

The rule that follows from it:

> **Nothing in this repo may be true of exactly one installation.**

Every host name, domain, IP address, MAC address, credential, cloud account,
tablet, and person is a *parameter*, supplied at deploy time by whoever is
deploying. The repo holds the shape of the system; the operator holds the values.

This is a product-vision constraint, not a security one. Several of the patterns
ruled out below are perfectly good security practice in a single-tenant repo.
They are excluded because they bake one operator into the artifact.

## The three categories

Every piece of configuration in Aerie falls into one of three buckets. Knowing
which one you're holding tells you where it goes.

| | Example | Where it lives |
|---|---|---|
| **Structural** — true for every installation | "the API talks to Postgres on 5432", the Quartz DDL, a Helm chart's shape, a pinned tool version | Committed to git |
| **Operator values** — differs per installation, not sensitive | base domain, LAN subnet, node IPs, host names, timezone, tablet count | GitHub Actions **variables** (`vars.*`), Helm `values.yaml`, script parameters — supplied per install |
| **Operator secrets** — differs per installation, sensitive | HA token, Route53 credentials, restic password, cluster token, Wi-Fi password | An external secret store, referenced by name from git |

The failure mode to watch for is a structural-looking file quietly carrying an
operator value — a hardcoded `192.168.x.x` in a script, a `landis.family` in a
manifest, a package name with a surname in it.

## No keys in the repo

**No secret material is committed to this repo — including encrypted secret
material.** That rules out SOPS + age, Sealed Secrets, git-crypt, and every other
"commit the ciphertext, hold the key elsewhere" pattern, notwithstanding that
SOPS-in-git is a mainstream and cryptographically sound GitOps convention.

The reason is redeployability, not cryptography. A committed encrypted secret is
*one operator's* secret sitting in the shared artifact. Every downstream user
inherits a file that is meaningless to them, must be told to delete it, and must
be walked through generating their own. A `.sops.yaml` creation rule is
architecture that only makes sense if the repo has exactly one owner.

Git may hold **pointers** to secrets — a parameter path, an `ExternalSecret`
reference, a variable name. Pointers are structural: they are identical for every
installation. The bytes behind them are not.

There is exactly one acceptable imperative secret injection per installation: the
bootstrap credential that lets the cluster reach its secret store. Everything
downstream of it is declarative and committed.

## Parameterization in practice

The pattern is already established and should be followed rather than reinvented.

**Domain.** `${DOMAIN}` is supplied from the `vars.DOMAIN` repository variable in
[`cd.yml`](../.github/workflows/cd.yml) and flows into compose files
([`compose.prod.yml`](../compose.prod.yml)) and app config. No document or
manifest hardcodes the base domain. Docs that need to show it write `<domain>`.

**Secrets.** Supplied as GitHub Actions repository secrets, injected as
environment at deploy time — `HA_TOKEN`, `RESTIC_PASSWORD`,
`K3S_CLUSTER_TOKEN`, `NODE_SSH_PRIVATE_KEY`, `KIOSK_RELEASE_*`. Never written to
the server's filesystem by the repo, never committed.

**Provisioning.** [`scripts/hyperv/`](../scripts/hyperv/) and
[`scripts/k3s/`](../scripts/k3s/) take every machine-specific fact as a named
parameter (`-VMName`, `-IPAddress`, `-NtpServer`, `-JoinServer`, disk sizes).
Nothing about the author's three hosts is embedded in them. Preserve this — it is
the single largest piece of already-portable work in the repo.

**Version pins are not parameters.** The k3s, Flux and qemu-img versions the
provisioning scripts install are committed in
[`scripts/versions.json`](../scripts/versions.json) and read by
[`scripts/lib/AerieVersions.ps1`](../scripts/lib/AerieVersions.ps1) — not
supplied as `vars.*`. They pass both tests below: a second operator's install
isn't *wrong* at these versions, and they aren't *meaningless* to them either —
they're the versions this repo was tested against, and asking a stranger to
invent their own is asking a question the repo should answer. Committing them
also binds the version to the commit, so a node rebuilt from an old tag gets
that tag's k3s rather than whatever the repository settings say today. The same
reasoning puts Helm chart versions in the manifests rather than in variables.

**Choice of platform is itself a parameter.** Where Aerie depends on an external
service, the *interface* is committed and the *provider* is chosen per install.
Requiring every future user to open an AWS account to run a home server would
defeat the entire premise. Cloud-backed choices need a self-hosted or alternate
path, even if the author's own installation uses the cloud one.

## Known debts

Honest accounting of things that currently violate the above. Not urgent, but
they should not grow.

- **Kiosk package name** — `family.landis.aeriekiosk` is compiled into the
  Android app ([`apps/kiosk/`](../apps/kiosk/)) and its Device Owner
  provisioning. Android package names are effectively immutable post-install, so
  this needs a deliberate rename to a neutral namespace before open-sourcing, not
  a parameter.
- **Docs carrying real values** — several architecture docs quote the author's
  actual domain, LAN addresses, and hardware. Fine for now; they need a pass
  before publication.
- **Personal seed data** — zones, devices, routines, and HA entity IDs that
  describe one specific house. Needs to become sample/`values.yaml` data.

## Applying this to a change

Before committing, ask of every new file:

1. Would this line be **wrong** for a second operator? If yes, it's a parameter.
2. Would this line be **meaningless** to a second operator? If yes, it's a
   parameter — encrypted operator secrets fail here even though they're safe.
3. Does this add a **mandatory** account, vendor, or purchase to the install? If
   yes, an alternate path is part of the work, not a follow-up.
4. Can a second operator run this **without asking the author a question**? If
   not, the missing answer belongs in a parameter or the README.

The measure of every phase in [`TODO_SWARM.md`](../TODO_SWARM.md) is not "does
the author's cluster run" but "could a stranger run this on their own three
boxes."
