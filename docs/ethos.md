# Hatch Ethos

## Summary

Hatch is not a deployment. It is a **product that produces deployments**.

The goal is to ship this repo as something a stranger can run themselves —
`docker compose up -d` on their own machine, under their own domain, with
their own data. That goal is not a future rewrite. It is a constraint on every
commit made today, because a repo that accumulates operator-specific facts
cannot be handed to a second operator without a painful excavation later.

The rule that follows from it:

> **Nothing in this repo may be true of exactly one installation.**

Every host name, domain, IP address, MAC address, credential, cloud account,
tablet, and person is a *parameter*, supplied at deploy time by whoever is
deploying. The repo holds the shape of the system; the operator holds the values.

This is a product-vision constraint, not a security one. Several of the patterns
ruled out below are perfectly good security practice in a single-tenant repo.
They are excluded because they bake one operator into the artifact.

This document is the **how** — the rule that constrains every commit,
regardless of which piece of Hatch it touches.

## The three categories

Every piece of configuration in Hatch falls into one of three buckets. Knowing
which one you're holding tells you where it goes.

| | Example | Where it lives |
|---|---|---|
| **Structural** — true for every installation | "the API talks to Postgres on 5432", the Quartz DDL, `compose.yaml`'s shape, a pinned tool version | Committed to git |
| **Operator values** — differs per installation, not sensitive | the base port (`HATCH_PORT`), the checkout path (`HATCH_CHECKOUT`), a runner's name | Environment variables, supplied per install — `compose.yaml` and `docs/hatch-at-home.md` list the full set |
| **Operator secrets** — differs per installation, sensitive | a Claude API key, a git push credential, an API key minted for an agent | `src/Hatch.Api/.env.json` (gitignored) or an environment variable, never committed |

The failure mode to watch for is a structural-looking file quietly carrying an
operator value — a hardcoded domain in a script, a real person's name in a
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

**Domain and port.** `HATCH_PORT` and the reverse-proxy domain in front of it
are environment variables, read by `compose.yaml` and documented in
[`hatch-at-home.md`](../src/Hatch.Web/apps/hatch/public/hatch-at-home.md). No
document or manifest hardcodes a real domain; docs that need to show one write
`<domain>` or `${DOMAIN}`.

**Secrets.** A Claude API key, a git push credential, and any key minted for an
agent are supplied as environment variables or in the gitignored
`src/Hatch.Api/.env.json`, never committed and never written into an image.

**Choice of platform is itself a parameter.** Where Hatch depends on an
external service, the *interface* is committed and the *provider* is chosen
per install. Cloud-backed choices need a self-hosted or alternate path, even if
one installation's own deployment uses the cloud one.

## Applying this to a change

Before committing, ask of every new file:

1. Would this line be **wrong** for a second operator? If yes, it's a parameter.
2. Would this line be **meaningless** to a second operator? If yes, it's a
   parameter — encrypted operator secrets fail here even though they're safe.
3. Does this add a **mandatory** account, vendor, or purchase to the install? If
   yes, an alternate path is part of the work, not a follow-up.
4. Can a second operator run this **without asking anybody a question**? If
   not, the missing answer belongs in a parameter or the README.

The measure of every change is not "does this installation run" but "could a
stranger run this on their own machine."
