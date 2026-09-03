[← Phase 1](phase-1-node-substrate.md) · [Design & decisions](design.md) · [Phase 3 →](phase-3-platform-services.md)

---

# Phase 2 — k3s + Flux + secrets

**Status: Complete**

> Needs Phase 1's two available-host VMs actually built and reachable over
> SSH first — the third host is still running prod and doesn't get a node
> until Phase 7.
>
> **`[x]` here means the script/workflow does it, not that the cluster's
> etcd has actually formed yet** — same convention as Phase 1.

## Phase 2 — The steps

- [x] `scripts/k3s/Install-K3sNode.ps1` — new script, same shape as
      `Initialize-AerieNode.ps1` (reuses `hyperv/lib/AerieSsh.ps1` to connect,
      takes `-VMName`/`-IPAddress` plus `-ClusterInit` or `-JoinServer
      <node1-ip>`), wrapped by the **Provision 1: Install k3s** workflow
      (`.github/workflows/provision-1-install-k3s.yml`) so both nodes are one
      dispatched run each rather than hand-typed SSH sessions:
      - node 1: `curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION=<pin> sh -s - server --cluster-init --disable servicelb --token <token>`
      - node 2: same, with `--server https://<node1-ip>:6443` in place of `--cluster-init`
      - **pin `INSTALL_K3S_VERSION`** — don't track the latest/stable channel,
        for the same reproducibility reason as Goal 6.5's Renovate ask, which
        should cover this pin too once it exists
        — *pinned in [`scripts/versions.json`](../../../scripts/versions.json), read by
        `scripts/lib/AerieVersions.ps1`. It started life as a `K3S_VERSION`
        repository variable; that was the wrong bucket by
        [`docs/ethos.md`](../../ethos.md)'s own table — a version pin is
        structural, identical for every installation, so it belongs in git.
        Committing it also ties the version to the commit: a node rebuilt from
        an old tag gets that tag's k3s, which a repo-settings value can't do.
        The same file now holds the Flux and qemu-img pins, and is the one
        target Goal 6.5's Renovate ask needs for the scripted tooling*
- [x] Generate the shared cluster token before either install (`openssl rand
      -hex 32`); store it as the `K3S_CLUSTER_TOKEN` repository secret —
      exactly like the SSH keys, never in git
- [x] Confirm node-to-node ports are open before the second node joins: TCP
      6443 (apiserver), 2379-2380 (etcd), 10250 (kubelet), UDP 8472 (flannel
      VXLAN). Debian/Ubuntu cloud images ship with no firewall active, so
      this is a no-op today — worth a one-line check, not a real risk, unless
      that default changes
      — *`Install-K3sNode.ps1`'s join preflight now checks the four TCP
      ports against `-JoinServer` before installing. UDP 8472 is left
      unchecked on purpose: a TCP connect can't probe a connectionless port,
      and the no-firewall default above is what makes that an acceptable gap
      rather than a real one*
- [x] ~~`age-keygen` for the SOPS key~~ — **cut.** No secret bytes in git,
      encrypted or otherwise; see [Secrets](design.md#secrets--no-bytes-in-git) and
      [`docs/ethos.md`](../../ethos.md). Replaced by the three items below
- [x] Dedicated `aerie-eso` IAM user — `ssm:GetParameter*` /
      `ssm:GetParametersByPath` scoped to `/aerie` **and** `/aerie/*`, plus
      `kms:Decrypt` restricted to SSM by a `kms:ViaService` condition.
      Separate from `aerie-restic` and the Route53 user, same isolation
      discipline as Phase 0. *Both* ARNs because listing authorizes against
      the bare path — `/aerie/*` alone denies `GetParametersByPath`, which is
      how the first Provision 2 run failed. The policies are committed as
      [`scripts/secrets/iam/`](../../../scripts/secrets/iam/) and applied by
      [`Set-AerieSecretsIam.ps1`](../../../scripts/secrets/Set-AerieSecretsIam.ps1)
      rather than retyped into the console
- [x] Define and commit the parameter naming convention
      (`/aerie/<component>/<key>`, e.g. `/aerie/ha/token`,
      `/aerie/cert-manager/route53-secret-access-key`). This is the *pointer*
      half — structural, identical for every installation, and belongs in git
      — *[`scripts/secrets/parameters.json`](../../../scripts/secrets/parameters.json)
      is the single source of truth (paths, which env var supplies each, which
      phase consumes it — no values, ever), read by the seeding script today
      and by the Phase 3 `ExternalSecret`s next. Convention, IAM split,
      rotation story and what deliberately stays **out** of the store are in
      [`docs/secrets-architecture.md`](../../secrets-architecture.md). The
      prefix is a parameter (`-ParameterPrefix`), so two installations can
      share one AWS account*
- [x] **Provision 2: Seed secrets** workflow
      (`.github/workflows/provision-2-seed-secrets.yml`) — pushes the existing
      GitHub Actions secrets into the `/aerie/*` tree
      (`aws ssm put-parameter --type SecureString --overwrite`), then creates
      the single ESO bootstrap Secret on the cluster over SSH. Idempotent and
      re-runnable, and the automation-first counterpart to doing it by hand —
      same convention as Provision 0 and 1. This is the **one** imperative
      secret injection the design allows
      — *[`scripts/secrets/Sync-AerieSecrets.ps1`](../../../scripts/secrets/Sync-AerieSecrets.ps1).
      Three refinements on the line above: it **reads before writing**, so an
      unchanged secret doesn't burn one of SSM's 100 versions per run;
      it **verifies as the `aerie-eso` identity**, listing *and* decrypting
      one value, since `GetParametersByPath` never touches KMS and would hide
      a missing `kms:Decrypt` until ESO failed on it in production; and no
      secret ever reaches a command line — values go to AWS via
      `--cli-input-json file://` and to the cluster over SSH **stdin**, which
      is why `Invoke-NodeSsh` grew `-StdIn` rather than using a heredoc*
- [x] ~~`flux bootstrap ...`, run once from an operator machine~~ — **scripted
      instead.** `Provision 3: Bootstrap Flux`
      ([`.github/workflows/provision-3-bootstrap-flux.yml`](../../../.github/workflows/provision-3-bootstrap-flux.yml)
      → [`scripts/flux/Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1))
      installs Flux *on the node* over SSH, so no cluster credential is copied
      onto a runner. Leaving this one imperative would have made it the only
      provisioning step with no repeatable path — and it's the one a rebuilt
      control plane most needs to re-run. Owner/repo come from the run's
      context, so it can only ever be pointed at the repo it was dispatched
      from. Everything Flux manages after this is a git commit to `deploy/`

      *Deliberately **not** `flux bootstrap github`.* Bootstrap's convenience is
      that it commits Flux's own manifests back here, and that is precisely
      what [ethos](../../ethos.md) forbids: `gotk-sync.yaml` carries one
      installation's owner/repo/branch, and `gotk-components.yaml` becomes a
      second pin for the Flux version [`scripts/versions.json`](../../../scripts/versions.json)
      already owns — 10k lines every downstream fork would re-conflict on at
      every re-run. The script does bootstrap's halves explicitly instead:
      `flux install` for the controllers, then `flux create source git` +
      `flux create kustomization` for what bootstrap would have serialized into
      `gotk-sync.yaml`. Those live in the cluster, and **the run writes nothing
      to git**. Two consequences worth noting: the PAT drops from
      contents:write + administration:write to *optional*, contents:read (a
      public repo is cloned anonymously, so `FLUX_GITHUB_TOKEN` can be unset
      entirely); and `versions.json` becomes the only place the Flux version
      exists, so an upgrade is a bump plus a re-dispatch rather than a
      committed manifest to keep in step
- [x] Note the gap, don't solve it here: `kubectl`/Flux target node 1's IP
      directly — there's no VIP in front of the apiserver itself (kube-vip in
      Phase 3 fronts *ingress* traffic only). Losing node 1 means manually
      repointing the kubeconfig context at another server. **Phase 7 did not
      close this**, and the sentence that used to say "until Phase 7 restores a
      third node" was wrong in a way worth correcting rather than letting
      expire: the third node restored *quorum tolerance* — etcd now survives
      losing one member — but the kubeconfig still names one machine, and
      losing that machine is still a manual repoint. Phase 9 inherits the
      question, which is the natural moment for it, since a kubeconfig is
      per-installation. Acceptable for a home cluster; call it out if that changes
      — *written up in
      [`docs/secrets-architecture.md`](../../secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver),
      with the recovery (both workflows are idempotent — re-run against a
      survivor) and the two ways out if it ever costs more than that. Both
      provisioning scripts print the warning at the end of a successful run,
      so it can't quietly become a surprise*

> During the parallel build only the two new servers exist as nodes. Two-node
> etcd has *worse* availability than one, so treat the build window as
> non-production and rebuild the old prod box as the third server immediately
> after cutover (Phase 7).


