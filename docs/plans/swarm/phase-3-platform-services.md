[← Phase 2](phase-2-k3s-flux-secrets.md) · [Design & decisions](design.md) · [Phase 4 →](phase-4-data-tier.md)

---

# Phase 3 — Platform services

**Status: Complete**

> Re-scoped. The first pass listed seven bullets that were really one sentence
> each; working them through against the repo turned up five things that would
> have stopped a run dead, and one dependency claim that was simply wrong.
>
> - **Operator values have no path into a Flux-reconciled manifest.** Phases 0-2
>   pass `${DOMAIN}` and friends as `vars.*` at deploy time, but Flux reconciles
>   from git, where [ethos](../../ethos.md) forbids them. Nothing in the plan
>   bridged that. Flux's answer is `postBuild.substituteFrom` against an
>   in-cluster ConfigMap — which has to exist *before* the first commit under
>   `deploy/`, making it the true first step of the phase (3b.1).
> - **The Longhorn disk is raw.** Phase 1 attaches a fixed VHDX and explicitly
>   leaves it unformatted — "Longhorn claims it in Phase 3"
>   ([Initialize-AerieNode.ps1:494](../../../scripts/hyperv/Initialize-AerieNode.ps1#L494)).
>   Longhorn's v1 engine wants a *filesystem path*, not a block device, and
>   defaults to `/var/lib/longhorn` on the root disk. Install it before the disk
>   is mounted there and it quietly fills the OS disk instead — 32 GB by
>   default, against a 200 GB data disk it never touches (3b.2).
> - **There is no LoadBalancer implementation in the cluster.** Phase 2 installs
>   k3s with `--disable servicelb` and the plan called kube-vip one bullet in a
>   list of four `HelmRelease`s. It is two components, not one, and until both
>   land Traefik's Service sits at `<pending>` and ingress has no address (3b.8).
> - **A wildcard cert that nothing serves.** Traefik falls back to its built-in
>   self-signed certificate for any route that doesn't name a Secret. Without a
>   `TLSStore`, the wildcard is issued, stored, and never presented (3b.10d).
> - **Split-horizon DNS breaks the DNS-01 self-check.** pfSense Unbound answers
>   for `${DOMAIN}` on the LAN, so cert-manager's propagation check — which
>   resolves through CoreDNS → the node's resolver → pfSense — never sees the
>   public `_acme-challenge` TXT it just wrote to Route53. The challenge hangs
>   and reports what looks like an AWS failure (3b.7).
> - **The `dependsOn` reasoning was wrong.** "cert-manager and CNPG both need
>   AWS credentials that only ESO can supply" — neither *controller* needs a
>   credential. The `ClusterIssuer` does, and the Phase 4 CNPG `Cluster` does.
>   Serializing controller installs behind ESO buys nothing and hides the real
>   constraint, which is that ESO's **CRDs** must exist before any
>   `ExternalSecret` applies. A two-layer Kustomization split expresses that
>   correctly; a chain of `dependsOn` between releases does not — and a Ready
>   `HelmRelease` never did imply a synced Secret (3b.3).

## [x] Phase 3a — Manual prerequisites

*Six one-time steps. None of them are code, all of them block something below.
Do these first, in order, and the whole of 3b runs unattended.*

**[x] 1. Confirm Phase 2 actually landed.** Phase 2's `[x]`s mean the scripts exist,
not that a cluster does. On any node, as `aerie`:

```sh
sudo k3s kubectl get nodes -o wide                    # 2 nodes, both Ready
sudo k3s kubectl -n external-secrets get secret aerie-eso-bootstrap
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux check
```

and confirm the cluster is actually pointed somewhere:

```sh
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux get sources git
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux get kustomizations
```

Both `flux-system`, both Ready. Note there is nothing to check *in the repo*:
Provision 3 runs `flux install` rather than `flux bootstrap`, so no
`deploy/cluster/flux-system/` is ever committed and the sync configuration
lives only in the cluster. Any of the five failing means re-running the
matching Provision workflow; all of them are idempotent, so a re-run is the fix
rather than a repair.

**[x] 2. Pick and reserve the ingress VIP.** This is the floating address kube-vip
answers ARP for, and the one pfSense will eventually point `*.${DOMAIN}` at in
Phase 7.

  1. pfSense → **Services → DHCP Server → LAN** — note the pool's start and end
     address.
  2. Choose an address on the node subnet that is **outside that pool** and is
     not one of the nodes' DHCP reservations from Phase 1.
  3. If the only convenient address is inside the pool, shrink the pool instead
     of taking it. kube-vip answers for the VIP unconditionally; a DHCP client
     later handed the same address is an intermittent outage that presents as a
     networking bug and costs a day.
  4. Do **not** create a reservation or static mapping for it. The VIP has no
     MAC of its own — it moves between nodes, which is the point.
  5. Verify it's genuinely free, from a LAN machine: `ping -c3 <vip>` gets no
     reply, and `arp -n <vip>` shows no entry.
  6. Record it. It becomes the `INGRESS_VIP` repository variable in step 5.

  Do not point any DNS at it yet — that's Phase 7, after the cluster serves.

**[x] 3. Read the node's network interface name.** kube-vip's ARP mode advertises on
a named interface.

```sh
ssh aerie@<node-ip> "ip -o -4 addr show scope global | awk '{print \$2, \$4}'"
```

Expect one line, e.g. `eth0` or `ens18`. It will be identical on every node —
they're built from one golden image — and if it isn't, stop and find out why
before continuing. Record it as `NODE_INTERFACE`.

**[x] 4. Collect the Route53 facts, and confirm the split-horizon problem is real.**

     the **Hosted zone ID** (`Z...`). Record it as `ROUTE53_HOSTED_ZONE_ID`.
  2. Confirm the zone is publicly delegated:
     `dig +short NS ${DOMAIN} @1.1.1.1` must return four `awsdns` nameservers.
     If it doesn't, DNS-01 cannot work — and neither can Caddy's certificates
     today, so this should already be true.
  3. Confirm the internal override, from a LAN machine:
     `dig +short A home.${DOMAIN}` returns an internal address while
     `dig +short A home.${DOMAIN} @1.1.1.1` returns nothing. That difference is
     expected and is exactly what 3b.7's resolver override exists to survive.

**[x] 5. Set the new repository variables.** Settings → Secrets and variables →
Actions → **Variables**. Provision 4 (3b.1) reads these and nothing else.

| Variable | Value | New? |
|---|---|---|
| `DOMAIN` | base domain | existing |
| `ACME_EMAIL` | Let's Encrypt account address | existing |
| `AWS_REGION` | region holding the SSM tree | existing |
| `INGRESS_VIP` | from step 2 | **new** |
| `NODE_INTERFACE` | from step 3 | **new** |
| `ROUTE53_HOSTED_ZONE_ID` | from step 4 | **new** |
| `LONGHORN_REPLICA_COUNT` | **`2`** — see 3b.11 | **new** |

**[x] 6. Look up and record the chart versions to pin.** Every `HelmRelease` below
pins an exact chart version, for the same reproducibility reason as the k3s
pin. Unlike everything else in 3a these are *structural* — identical for every
installation — so they are committed in the manifests, exactly like the k3s,
Flux and qemu-img pins in [`scripts/versions.json`](../../../scripts/versions.json), and
not entered as variables. Collect them once so 3b is a straight line:

| Component | Chart repository | Chart | App | Step |
|---|---|---|---|---|
| external-secrets | `https://charts.external-secrets.io` | `2.8.0` | `v2.8.0` | 3b.4 |
| cert-manager | `https://charts.jetstack.io` | `v1.21.1` | `v1.21.1` | 3b.7 |
| kube-vip | `https://kube-vip.github.io/helm-charts` | `0.11.0` | `v1.2.2` → pin `image.tag: v1.2.3` | 3b.8 |
| kube-vip-cloud-provider | `https://kube-vip.github.io/helm-charts` | `0.2.10` | `v0.0.12` | 3b.8 |
| longhorn | `https://charts.longhorn.io` | `1.11.3` | `v1.11.3` | 3b.11 |
| cloudnative-pg | `https://cloudnative-pg.github.io/charts` | `0.29.0` | `1.30.0` | 3b.12 |

Read from each repository's `index.yaml` on 2026-08-15, and chosen against the
k3s pin in [`scripts/versions.json`](../../../scripts/versions.json) (`v1.35.7+k3s1`) on
the same soak reasoning that pin carries — newest is not the goal, *known* is:

- **external-secrets `2.8.0`** over the same-week `2.9.0`. Neither declares a
  breaking change and the chart floor is only `>= 1.19`, so this is soak alone.
  `2.8.0` is also the release that added the OpenBao provider, which is the
  eventual destination of 3b.5's isolated `ClusterSecretStore`. `installCRDs`
  is still a real top-level value in this chart, so 3b.4's wording holds.
- **cert-manager `v1.21.1`** supports Kubernetes 1.33–1.36 and is the last patch
  of a line five weeks old. `v1.20` goes EOL at the 1.22 release and only the
  final patch of a branch is supported upstream, so the older line buys nothing.
- **kube-vip `0.11.0`** is the only chart carrying the v1.2 line, and v1.2 is
  where the service handling was refactored — the exact path `svc_enable` uses.
  Its `appVersion` is `v1.2.2`, but `v1.2.3` fixes regressions in lease
  management and route cleanup introduced by that refactor, so set `image.tag`
  explicitly and drop the override once a chart ships with it as the default.
  Staying on `0.10.0`/`v1.1.2` was the alternative: pre-refactor and stable
  since March, but two minors back and receiving no fixes.
- **longhorn `1.11.3`**, which upstream lists as the stable, widely-adopted
  release; `1.12.1` is a day old. `1.11.3` requires Kubernetes ≥ 1.34, which the
  k3s pin satisfies, and its node prerequisites are exactly 3b.2's package list.
- **cloudnative-pg `0.29.0`** (operator 1.30) supports Kubernetes 1.34–1.36.
  The `0.28.x` line ships operator 1.29, which supports 1.33–1.35 but goes EOL
  on 2026-09-29 — inside this build window, so it would be an upgrade before
  Phase 4 finished rather than after.

> [x] **Gate:** the seven variables in step 5 are all set, and step 2's address
> answers nothing on the LAN. 3b assumes both.

## Phase 3b — Scriptable, in this order

*Each step's inputs come from the repo or from the ConfigMap planted in step 1 —
nothing below waits on a human except the one explicit stop in step 10.*

- [x] **1. Provision 4: cluster configuration.**
      [`.github/workflows/provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml)
      → [`scripts/k3s/Set-ClusterConfig.ps1`](../../../scripts/k3s/Set-ClusterConfig.ps1).
      Renders ConfigMap
      `aerie-cluster-config` in `flux-system` from the 3a.5 variables and
      applies it over SSH stdin — same shape as Provision 2's bootstrap Secret,
      minus the secrecy, since every one of these is an operator *value*.
      Every Kustomization in `deploy/` then reaches them via
      `postBuild.substituteFrom`.
      **Runs before the first commit under `deploy/`**: a Kustomization whose
      substitution source is missing fails to reconcile rather than degrading
      gracefully. Re-runnable — changing a value and re-running is how the VIP
      or domain gets changed later, with no commit.
      *Exit:* `kubectl -n flux-system get cm aerie-cluster-config -o yaml` lists
      all seven keys.
      — *Three things the one-line version didn't say.
      [`scripts/k3s/cluster-config.json`](../../../scripts/k3s/cluster-config.json) is
      the committed pointer half — which keys exist, which variable supplies
      each, what shape a valid value has — the same split as
      [`parameters.json`](../../../scripts/secrets/parameters.json), so the workflow
      only ever gains a `vars.` line and the script learns the rest from the
      file. **Values are validated before anything is applied**, and
      case-sensitively: PowerShell's `-match` is case-**in**sensitive, so
      `^Z[A-Z0-9]+$` silently accepted a lowercased hosted zone id, which is a
      zone that doesn't exist and surfaces from inside cert-manager at 3b.10 as
      `NoSuchHostedZone`. A domain, being case-insensitive by RFC, is folded
      rather than rejected. And the two values the **node** can settle are
      checked against it: `NODE_INTERFACE` must exist (the failure lists the
      node's real interfaces), and `INGRESS_VIP` must sit on that interface's
      subnet and not be the node's own address — kube-vip's ARP mode installs
      cleanly against a wrong interface and simply never answers, which is a
      day spent on what presents as a networking bug. `preflight_only` runs
      every check and the live diff without applying, which is what to dispatch
      before changing a value on a cluster that's already serving.*
- [x] **2. Provision 5: node storage prep.**
      [`.github/workflows/provision-5-node-storage.yml`](../../../.github/workflows/provision-5-node-storage.yml)
      → [`scripts/k3s/Initialize-NodeStorage.ps1`](../../../scripts/k3s/Initialize-NodeStorage.ps1),
      run once per node. Over SSH:
      - install `open-iscsi`, `nfs-common`, `cryptsetup`; `systemctl enable
        --now iscsid`. Longhorn attaches volumes over iSCSI to the *host*, so
        these are node packages, not container ones
      - confirm `multipath-tools` is absent (it is, on a Debian cloud image) or
        blacklist Longhorn's devices — multipathd claiming them is the classic
        "volume stuck in Attaching" failure
      - identify the data disk **by being the unpartitioned one of the expected
        size**, never by hardcoding `/dev/sdb`; refuse ambiguity and refuse a
        disk that already holds a filesystem unless `-Force`
      - `mkfs.ext4`, label it, and mount at `/var/lib/longhorn` from `/etc/fstab`
        **by UUID** — Hyper-V device ordering isn't stable across reboots, and a
        `/dev/sdb` fstab entry is a node that boots with Longhorn's data path
        pointing at the wrong disk
      **Before step 11**, unavoidably: Longhorn's default data path is on the
      root filesystem, so installing it first means silently filling the OS disk.
      *Exit:* `findmnt /var/lib/longhorn` on every node, with capacity matching
      `-DataDiskSizeGB`.
      — *Four things the bullets above didn't say. **The disk is never named,
      and neither is it guessed from one signal**: the script takes whatever is
      already mounted, else the device carrying the `longhorn` label, else the
      one empty unpartitioned disk of about the expected size — and refuses
      ambiguity at every level, including two disks wearing the label. Two
      rules survive `-Force`, which otherwise only permits wiping an idle
      formatted disk: a disk with anything mounted from it is never a
      candidate, and neither is the one holding `/`. **The node has to agree it
      is the node you named** — `vm_name` is checked against its hostname
      before anything is written, because a wrong address here formats a disk
      on the wrong machine. **`nofail` is in the fstab entry on purpose**: no
      `nofail` means a missing disk halts boot in emergency mode on a headless
      VM, so instead the node boots and the empty mount point underneath is
      left `chattr +i`, which is what actually stops Longhorn writing to the OS
      disk when the mount is absent — it gets `EPERM` rather than free space.
      (Verified on a live kernel: mounting over an immutable directory works,
      writing into it unmounted does not.) And **`findmnt --verify` runs after
      the mount, not before** — it counts a not-yet-existing mount point as an
      error, and what it adds over `mount` having worked is the boot-time
      reading: duplicate targets, an unresolvable UUID.*
- [x] **3. The Flux tree skeleton** — commit only, no cluster access:
      - `deploy/cluster/kustomization.yaml` — the root Flux reconciles.
        Already committed as an empty-`resources` skeleton, since Provision 3
        neither creates nor writes to this path. There is deliberately no
        `flux-system/` directory: the controllers come from the pin in
        `scripts/versions.json` and the sync config lives in the cluster
      - `deploy/cluster/infrastructure.yaml` — two Kustomizations,
        `infra-config` `dependsOn` `infra-controllers`
      - `deploy/cluster/infrastructure/controllers/` — one `HelmRepository` +
        `HelmRelease` per component (steps 4, 7, 8, 11, 12)
      - `deploy/cluster/infrastructure/config/` — the objects those controllers'
        CRDs define (steps 5, 6, 9, 10, and Longhorn's StorageClasses)

      Both Kustomizations carry `postBuild.substituteFrom` the step-1 ConfigMap.
      The layer boundary is what enforces ordering: CRDs and controllers
      converge first, then everything that needs them. This replaces the
      original plan's release-to-release `dependsOn` chain, for the reason in
      the preamble.
      — *Two things the bullets above didn't say. **`dependsOn` alone does not
      order anything here** — a Kustomization without `wait: true` reports Ready
      as soon as its objects are *applied*, which for a `HelmRelease` means the
      CR exists, not that the chart installed or that one CRD landed.
      `infra-config` would then start against a cluster with none of the types
      it uses, which is the exact failure the two-layer split exists to prevent.
      Both layers carry `wait: true`; `infra-controllers` gets a 10-minute
      timeout to go with it, because Longhorn's DaemonSets and engine images
      take minutes on a cold node and a short timeout reports slowness as
      failure and then blocks the other layer behind it. Expect `infra-config`
      to sit NotReady through step 10's deliberate stop at the staging issuer —
      that is the report, not a fault. And **`postBuild` substitution is not
      scoped to the tokens we chose**: Flux expands every `$VAR` in the built
      output, so an upstream chart value containing a bare `$` needs `$$` or the
      `kustomize.toolkit.fluxcd.io/substitute: disabled` annotation — while an
      undefined token expands to an empty string rather than erroring, so
      `${DOMIAN}` is not a failed reconciliation, it is an Ingress with no host.
      Both notes are written into the manifests themselves, since steps 4–12 are
      where they get paid for. The matching automation is a `deploy-manifests`
      job in [`ci.yml`](../../../.github/workflows/ci.yml) that builds every directory
      under `deploy/` carrying a `kustomization.yaml`, discovered rather than
      listed: from here on the tree reaches the cluster by commit alone, with no
      apply step in front of a human, so a kustomization that doesn't build is
      otherwise found as a Flux Kustomization that quietly stopped reconciling.
      It does not validate Flux's CRD schemas — a misspelled `spec` field still
      passes, and step 13 against a real cluster is what catches those.*
- [x] **4. External Secrets Operator** —
      [`controllers/external-secrets.yaml`](../../../deploy/cluster/infrastructure/controllers/external-secrets.yaml),
      pinned, `installCRDs: true`. Commit the `external-secrets` Namespace too
      even though Provision 2 already created it: applying an existing namespace
      is a no-op, and a rebuilt cluster shouldn't depend on which of the two ran
      first.
      *Exit:* `kubectl get crd externalsecrets.external-secrets.io`.
      — *Four things the bullets above didn't say. **The Namespace being
      committed has a cost, and it is the one Secret nothing can recreate**:
      `infra-controllers` prunes, so deleting this file deletes the namespace
      and `aerie-eso-bootstrap` with it, and the only way back is Provision 2's
      `bootstrap-only` stage. **`installCRDs: true` is already the chart
      default and is set anyway** — a default that flips upstream would take
      the CRDs, and therefore every object under `config/`, in a bump that read
      as routine. This chart templates its CRDs rather than shipping a `crds/`
      directory, so Helm upgrades them like any other resource (the usual Helm
      CRD caveat doesn't apply) and `spec.install.crds` / `spec.upgrade.crds`
      are deliberately absent: they govern a directory this chart doesn't use,
      so setting them would look like configuration while doing nothing.
      **Nothing here waits on cert-manager**, despite the release installing a
      `failurePolicy: Fail` webhook over `SecretStore` and `ExternalSecret` —
      the serving certificate comes from ESO's own bundled cert-controller,
      which is why 3b.4 and 3b.7 can share a layer with no ordering between
      them. What that webhook does mean is that step 5 cannot apply against a
      cluster where it isn't serving yet: `infra-controllers`' `wait: true`
      plus helm-controller's own default wait is the whole reason the next step
      works. And **`timeout` and `retries` are set against the layer's budget,
      not in isolation** — the 10m on `infra-controllers` is shared by every
      release in that directory, so an unbounded remediation loop here doesn't
      fail alone, it starves Longhorn behind it. One retry inside a four-minute
      ceiling absorbs a timed-out image pull and still reports a real failure
      as a failure, inside the window. (One note for step 5, from reading the
      installed CRDs: `external-secrets.io/v1` is the served and stored
      version, `v1beta1` is gated behind `crds.unsafeServeV1Beta1: false`, and
      the webhook rules match `v1` only — so a store written against the
      `v1beta1` examples still findable in older docs fails as an unknown API
      version.)*
- [x] **5. `ClusterSecretStore`** —
      [`config/cluster-secret-store.yaml`](../../../deploy/cluster/infrastructure/config/cluster-secret-store.yaml),
      **alone in its own file, nothing else beside it**. AWS provider, service
      `ParameterStore`, region `${AWS_REGION}`, authenticating by `secretRef` to
      `aerie-eso-bootstrap`. This is the single file that changes when the
      provider is swapped for in-cluster OpenBao before open-sourcing, which is
      the only reason it's isolated.
      *Exit:* `kubectl get clustersecretstore aerie-secrets` reports
      `Ready=True`.
      — *Four things the bullets above didn't say. **The store's name carries no
      provider**, which is what actually makes the isolation real: every
      `ExternalSecret` from step 6 onward names it in `secretStoreRef`, so a
      store called `aws-parameter-store` would turn the one-file OpenBao swap
      into a rename across the whole tree. It is `aerie-secrets`. **Omitting
      `namespace` from the two `secretRef`s is not an error, and that is the
      trap** — on a `ClusterSecretStore` it means *referent auth*: resolve the
      Secret in each consuming `ExternalSecret`'s own namespace. ESO then skips
      validation entirely and the controller still marks the store
      `Ready=True, Valid`, so this step's exit criterion is satisfiable by a
      store that has never once looked at a credential, with the failure
      surfacing later as every `ExternalSecret` unable to find a Secret sitting
      in `external-secrets`. Both refs name it. **`Ready=True` is enforced but
      cheap.** Enforced: the CR sets no `Reconciling`/`Stalled` condition and no
      `observedGeneration`, so kstatus falls through to its last rule — a plain
      `Ready` condition — and `infra-config`'s `wait: true` holds on it, which
      is the layer's design working without being told about this object.
      Cheap: it proves the region string resolves to an endpoint, the bootstrap
      Secret exists and both keys are readable, and nothing else. Static
      credentials are validated by *retrieving* them locally, which never calls
      AWS, so an expired key, a policy missing the bare-path ARN, or a tree
      seeded into another region all report a perfectly Ready store. Step 6's
      first synced `ExternalSecret` is the earliest honest proof — the argument
      for creating `ha/token` and the shipper token now, while nothing consumes
      them. And **two fields are deliberately absent**: `prefix`, which would
      let step 6 carry bare keys at the cost of `/aerie` living both here and
      in [`parameters.json`](../../../scripts/secrets/parameters.json), the one file both
      halves are supposed to read; and `conditions`, since scoping the store to
      a namespace list would have to be edited by Phases 4–8 in turn to guard
      against a tenant creating an `ExternalSecret` — not the threat model of a
      cluster where write access to this repository is strictly more powerful
      than the store.*
- [x] **6. `ExternalSecret`s** —
      [`config/external-secrets/`](../../../deploy/cluster/infrastructure/config/external-secrets/),
      one per entry in
      [`parameters.json`](../../../scripts/secrets/parameters.json) that is
      **`required: true`**. That rule is the correction to the original list,
      which named the kiosk Wi-Fi password and CNPG's S3 WAL credentials: both
      are `required: false`, neither is seeded, and an `ExternalSecret` pointing
      at an absent parameter sits in `SecretSyncError` indefinitely and poisons
      the phase gate. They move to the phases that create their IAM users and
      consume them — 5 and 4.
      Phase 3 therefore creates: `cert-manager/route53-*` (consumed here),
      plus `ha/token` and `logging/vm-log-shipper-token` — not consumed until
      Phase 5, but created now as the end-to-end proof that the store works
      while there's still nothing depending on it.
      Worth a small generator
      ([`scripts/secrets/New-ExternalSecrets.ps1`](../../../scripts/secrets/New-ExternalSecrets.ps1))
      plus a CI check that the tree matches `parameters.json`: the docs already
      promise both halves read one file, and hand-maintained duplication is how
      that stops being true.
      *Exit:* every `ExternalSecret` reports `SecretSynced` —
      `kubectl get externalsecrets -A`. This is the first thing in the build
      that has actually called AWS.
      — *Four things the bullets above didn't say. **`required: true` is the
      rule and it is not the whole rule**, which the paragraph above shows
      without noticing: `backup/*` is required, is seeded by every Provision 2
      run, and gets no manifest here, because its CronJob and the namespace it
      runs in are Phase 8. Left as prose, the rule and the enumeration disagree
      and whichever a future reader trusts wins silently. So the switch is an
      explicit `kubernetes` block on the entry — namespace, Secret name, key —
      and the generator enforces the implication in **both** directions: a
      block on a `required: false` entry is refused (it is the
      `SecretSyncError` that never clears, and `infra-config`'s `wait: true`
      turns that into a stuck phase gate), and a required entry with no block
      must carry a `kubernetesDeferred` note naming the phase that adds one. A
      value seeded on every run and read by nothing is otherwise
      indistinguishable, from every angle, from a value that works. **A
      credential pair is one Secret with two keys**, so the grouping is by
      (namespace, Secret name) rather than one object per parameter — the two
      halves of an AWS key are useless apart, and separate objects are how a
      rotation reaches one and not the other. That is also why the generator
      refuses two parameters claiming the same key inside one Secret, which
      would otherwise resolve as a silent overwrite at sync time. **The two
      proof secrets needed a namespace, so Phase 3 now creates the app's** —
      [`config/namespaces.yaml`](../../../deploy/cluster/infrastructure/config/namespaces.yaml),
      `aerie`, which Phase 5's chart must therefore target rather than create,
      or two owners fight over one object. No ordering is needed against the
      `ExternalSecret`s beside it: kustomize-controller applies Namespaces and
      CRDs as a first stage and waits for them before the rest, which is the
      same property the whole two-layer split leans on. The one namespace
      **not** created here is `cert-manager` — it arrives with the component
      that owns it in 3b.7, exactly as `external-secrets` does — so between this
      commit and that one, `route53-credentials` is a single object
      `infra-config` cannot apply. Expected, and it resolves itself. And
      **`deletionPolicy: Retain` is set explicitly for a failure that is
      otherwise silent**: it is the default, but if it ever weren't, a
      parameter that disappeared upstream would take a live credential out from
      under a running workload rather than reporting a sync error over the last
      good value. `creationPolicy: Owner` is the opposite trade and deliberate:
      deleting a manifest deletes its Secret, which is what keeps `prune: true`
      honest.*
- [x] **7. cert-manager** —
      [`controllers/cert-manager.yaml`](../../../deploy/cluster/infrastructure/controllers/cert-manager.yaml),
      controller only; the issuer comes in step 10.
      Pinned, `crds.enabled: true`, and the two `extraArgs` that 3a.4 exists to
      justify: `--dns01-recursive-nameservers-only` and
      `--dns01-recursive-nameservers=1.1.1.1:53,8.8.8.8:53`.
      Without them the propagation self-check resolves through CoreDNS → the
      node's resolver → pfSense Unbound, which answers authoritatively for the
      *internal* view of `${DOMAIN}` and will never return the public TXT
      record cert-manager just wrote. The order then hangs until timeout and
      reports what reads like a Route53 permissions failure.
      *Exit:* `kubectl get crd certificates.cert-manager.io`, and
      `kubectl -n cert-manager get helmrelease cert-manager` Ready.
      — *Four things the bullets above didn't say. **Those two flags are set as
      chart values, not `extraArgs`** — the chart has carried first-class
      `dns01RecursiveNameserversOnly` / `dns01RecursiveNameservers` for years,
      and they render precisely the flags named above. It matters because this
      chart ships a `values.schema.json` with `additionalProperties: false`, so
      a mistyped value fails the install; `extraArgs` is an unvalidated list of
      strings, where the same typo is a controller that starts cleanly and
      simply never reads the setting — which is indistinguishable, from the
      outside, from the split-horizon failure it was supposed to fix. **The CRD
      values do not have the shape the neighbouring file does**, which is the
      trap in reading 3b.4 and 3b.7 together: here `installCRDs` is deprecated
      and defaults to `false`, and `crds.enabled` is what it aliases. Its
      partner `crds.keep: true` is a default set explicitly for a failure with
      no other guard — helm-controller's `install.remediation` **uninstalls**
      before it retries, so without `keep` a single timed-out image pull would
      drop the CRDs on the way out and the garbage collector would take every
      `Certificate`, `Issuer` and `ClusterIssuer` in the cluster with them. The
      retry then succeeds, and the damage looks unrelated to it. **`clusterResourceNamespace`
      is the invisible link between 3b.6 and 3b.10** — a `ClusterIssuer` is
      cluster-scoped, so the `secretRef`s in step 10 carry no namespace and
      resolve in this one for every issuer in the cluster. It already defaults
      to the release's namespace, which is already where 3b.6 put
      `route53-credentials`, so writing it out changes nothing and is the only
      place that coupling is visible from. And **this file is what makes 3b.6
      whole**: `cert-manager-route53-credentials.yaml` targets a namespace that
      only arrives with the component owning it, so between that commit and this
      one `infra-config` had exactly one object it could not apply. Expected
      there, resolved here. The budget note is on the release itself — `timeout:
      5m` rather than 3b.4's `4m`, because this chart's install ends in a
      post-install hook (`startupapicheck`) that holds success until the webhook
      actually answers. That wait is kept deliberately: it is what makes a Ready
      `HelmRelease` here mean "the API accepts Certificates", which is the exact
      precondition step 10 needs and what `infra-config`'s `dependsOn` is waiting
      to be told.*
- [x] **8. kube-vip** — two components, which the original single bullet hid:
      - **`kube-vip-cloud-provider`**, which assigns addresses to
        `Service type=LoadBalancer`. Phase 2's `--disable servicelb` means the
        cluster has no such implementation at all right now
      - **`kube-vip`** DaemonSet in **ARP** mode — `vip_interface:
        ${NODE_INTERFACE}`, `svc_enable: true`, hostNetwork, plus its RBAC
      - the pool: ConfigMap `kubevip` in `kube-system` with
        `range-global: ${INGRESS_VIP}-${INGRESS_VIP}`, a deliberate
        one-address pool
      Still **not** an apiserver VIP — the Phase 2 gap
      ([docs](../../secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver))
      is unchanged by this and stays open until Phase 7.
      *Exit:* `ping ${INGRESS_VIP}` answers from the LAN, and
      `ip addr show ${NODE_INTERFACE}` on the elected leader shows it.
      — *[`controllers/kube-vip.yaml`](../../../deploy/cluster/infrastructure/controllers/kube-vip.yaml),
      one file for both releases, plus the pool ConfigMap. Three things the
      bullets above didn't say. **`configMapName` and the auto-created
      ConfigMap are two different objects, not one** — the cloud-provider
      chart's own template always names what it creates after the release
      (`kube-vip-cloud-provider`), ignoring `configMapName` entirely; that
      field only changes which name the *Deployment* looks for. Getting this
      backwards ships a correctly-named-but-empty ConfigMap the Deployment
      never reads, sitting right next to the one it does — so `cm.data` is
      left empty and the pool is a plain manifest named `kubevip` instead,
      which is also what makes the bullet above literally true rather than
      approximately true. **Neither chart carries a `values.schema.json`** —
      checked directly against both at the pinned versions, not assumed —
      so every key in both `values:` blocks is exactly as unvalidated as
      3b.4's `extraArgs` warning describes: a typo is a controller that
      installs cleanly and never reads the setting. And **the chart's
      defaults for every election-related env key
      (`svc_election: false`, `vip_leaderelection: false`) are not "election
      off"** — that reading would mean every DaemonSet pod ARP-announces the
      same address at once, an actual IP conflict, and is wrong. Traced
      through the release's own source at the pinned appVersion rather than
      trusted from the values file: with `svc_election: false`, kube-vip's ARP
      worker (`pkg/manager/worker/arp.go`, `StartServices`) falls back to what
      its own code calls `GlobalLeader` — a separate, always-on Kubernetes
      Lease election every kube-vip pod participates in, independent of
      `vip_leaderelection` (which only governs the control-plane VIP path,
      unused here since `cp_enable` stays false) and of `svc_election` (which
      would give each Service its own lease — no benefit with the single
      deliberate address this pool hands out). That Lease is what the exit
      criterion's "elected leader" is actually reading, and it's the same
      mechanism Phase 7's HA proof exercises with a hard power-off.*
- [x] **9. Traefik `HelmChartConfig`** — `config/traefik-helmchartconfig.yaml`,
      `helm.cattle.io/v1`, named `traefik` in `kube-system`. k3s's bundled
      Traefik is a `HelmChart` CR owned by k3s's own helm-controller;
      `HelmChartConfig` merges values into it, which is what keeps Flux and k3s
      from fighting over one release. Sets the Service's requested
      `${INGRESS_VIP}`, `websecure` TLS, and `publishedService`.
      *Exit:* `kubectl -n kube-system get svc traefik` shows
      `EXTERNAL-IP = ${INGRESS_VIP}`, and `curl -k https://${INGRESS_VIP}` from
      the LAN returns Traefik's 404 — the correct answer with no routes defined.
      — *Verified against the exact pin, not generic docs: k3s v1.35.7+k3s1's
      own `manifests/traefik.yaml` resolves chart `traefik-40.1.4+up40.1.0`
      (upstream `traefik-helm-chart` v40.1.0), which nests TLS at
      `ports.websecure.http.tls.enabled` — not the flatter, still commonly
      documented `ports.websecure.tls.enabled` from older chart lines. **Two
      corrections, both found on 2026-08-16 while diagnosing why the VIP
      refused every connection.** First, this chart is not schema-validated at
      all: the Rancher repackage k3s serves ships no `values.schema.json`
      (verified by extracting it from
      `/var/lib/rancher/k3s/server/static/charts`), so the
      `additionalProperties: false` rejection claimed here never happens and
      the legacy path would be accepted and silently ignored. Second, the VIP
      is now set as the `kube-vip.io/loadbalancerIPs` annotation via
      `service.annotations`, **not** as `spec.loadBalancerIP`. The deprecated
      field does work — kube-vip-cloud-provider's
      `checkLegacyLoadBalancerIPAnnotation` copies it into that same annotation
      (`pkg/provider/loadBalancer.go` at the pinned `v0.0.12`) — but only
      *after* Helm has rendered the Service without it, and in that window
      kube-vip's watcher sees an address-less Service, builds an instance with
      `addresses=[] hostnames=[]`, and dies on `lookup : no such host` before
      it patches `status.loadBalancer.ingress`. The VIP lands on the interface
      and answers ARP, the Service sits `<pending>` forever, kube-proxy
      programs nothing, and every connection to `${INGRESS_VIP}:443` is refused
      by the node's own stack with every object Ready. Rendering the annotation
      as part of the Service removes the window.
      `publishedService.enabled` is already set by k3s's
      own base `valuesContent`, set again here anyway since the manifest is
      this step's audit trail against the bullet above it. And
      `HelmChartConfig` carries no status subresource at all (checked against
      `k3s-io/helm-controller`'s Go types) — `infra-config`'s `wait: true`
      reports this object Ready the instant it applies, whether or not k3s's
      own controller has run the Job yet, so this step's *Exit* line above is
      the only real proof, the same gap 3b.5 documents for
      `ClusterSecretStore`.*
- [x] **10. The wildcard certificate — staging, then prod.** The one step in 3b
      with a human in the middle, deliberately:
      1. Both `ClusterIssuer`s (`letsencrypt-staging`, `letsencrypt-prod`),
         identical but for the ACME server URL. Route53 DNS-01 solver,
         `hostedZoneID: ${ROUTE53_HOSTED_ZONE_ID}`, `email: ${ACME_EMAIL}`,
         both halves of the credential by `secretRef` to step 6's Secret —
         the access key ID isn't sensitive, but splitting one credential across
         two delivery mechanisms is how drift starts.
      2. `Certificate` `aerie-wildcard` in `kube-system`, `secretName:
         aerie-wildcard-tls`, `dnsNames: ["*.${DOMAIN}", "${DOMAIN}"]` —
         **the apex is listed explicitly because a wildcard does not match it**.
      3. **Stop here and read `kubectl describe certificate aerie-wildcard`.**
         A stuck `DNS01` challenge is either 3a.4's split-horizon problem or a
         hosted-zone-ID / IAM mismatch. Diagnose it against staging, which has
         no meaningful rate limit. Then flip `issuerRef` to `letsencrypt-prod`
         and delete the staging Secret so a fresh order runs. Let's Encrypt
         allows five duplicate certificates per week; debugging DNS-01 against
         prod burns that in an afternoon and then you wait for it.
      4. Traefik `TLSStore` named `default` in `kube-system`, with
         `defaultCertificate.secretName: aerie-wildcard-tls`. **Missing from the
         original plan, and without it the certificate is issued and never
         served** — Traefik presents its built-in self-signed cert to any route
         that doesn't name a Secret, so every Phase 5 Ingress would need its own
         `tls:` block. One `TLSStore` covers all seven hostnames and everything
         added later, which is the entire argument for a wildcard.
      *Exit:* `openssl s_client -connect ${INGRESS_VIP}:443 -servername
      home.${DOMAIN} </dev/null | openssl x509 -noout -issuer -ext
      subjectAltName` shows a Let's Encrypt production issuer and `*.${DOMAIN}`.
      — *Committed as three files, not one:
      [`config/cluster-issuers.yaml`](../../../deploy/cluster/infrastructure/config/cluster-issuers.yaml)
      (the pair that must stay identical),
      [`config/wildcard-certificate.yaml`](../../../deploy/cluster/infrastructure/config/wildcard-certificate.yaml)
      (the object the stop is in) and
      [`config/traefik-tlsstore.yaml`](../../../deploy/cluster/infrastructure/config/traefik-tlsstore.yaml)
      (what serves the result) — so the staging-to-production flip is a one-word
      diff in a file that changes nothing else. **This step stays unticked until
      that flip is committed**, which is the only entry in 3b whose `[ ]` means a
      human, not code. Four things the bullets above didn't say. **The apex costs
      a second challenge at the same record name, and that is only safe for a
      reason worth naming**: both identifiers authorize through
      `_acme-challenge.${DOMAIN}` — the wildcard's challenge is not
      `_acme-challenge.*.${DOMAIN}` — with different tokens, so two TXT values
      are needed at one name, and cert-manager's Route53 solver UPSERTs a
      single-valued record set without merging what is already there (read at the
      pin, not assumed). Run concurrently they would overwrite each other and
      both fail; they never are, because the challenge scheduler refuses to
      process two challenges sharing a DNS name and type at once. The cost is
      that issuance is two serialized propagation waits, which routinely outruns
      `infra-config`'s 5m timeout on a first order — the NotReady the preamble
      predicts, arriving for a more specific reason than "the stop". **Step 3's
      "delete the staging Secret" is unnecessary, and it is not free.** A changed
      `issuerRef` is itself a re-issuance trigger — `SecretIssuerAnnotationsMismatch`
      is in the trigger policy chain, matching the issuer annotation cert-manager
      stamps on the Secret — so the flip re-orders on its own. Deleting the
      Secret first leaves the cluster with no default certificate until the new
      order completes, which is Traefik answering 443 with its self-signed one
      for the length of a DNS-01 round trip. **The `TLSStore` has a
      cluster-uniqueness rule with a silent failure behind it**: Traefik
      special-cases the name `default` so the store is keyed as `default` rather
      than by namespace/name, and finding two of them in different namespaces it
      deletes the default store outright and logs — every hostname in the cluster
      silently back on the self-signed certificate. It also resolves
      `defaultCertificate.secretName` in its own namespace with no
      cross-namespace path in the call at all, which is what puts the
      `Certificate` in `kube-system` rather than somewhere tidier: the store must
      sit beside the Traefik k3s ships, and the Secret must sit beside the store.
      And **the two issuers' account keys are the one difference that is
      load-bearing** rather than cosmetic — an ACME account key is registered
      with one server, so pointing both at one Secret makes whichever reconciles
      second fail against a key already registered elsewhere, reported as an
      account error over a Secret that plainly contains a key.*
- [x] **11. Longhorn** — `defaultDataPath: /var/lib/longhorn` matching step 2,
      and two settings that are easy to get wrong:
      - `defaultReplicaCount: ${LONGHORN_REPLICA_COUNT}` — **2 during the build
        window.** Only two nodes exist until Phase 7; a three-replica volume on
        a two-node cluster is permanently Degraded, and its alarms are noise
        that trains you to ignore the real ones. Phase 7 raises it
      - `persistence.defaultClass: false` — k3s already ships `local-path` as
        the default StorageClass, and the storage split deliberately puts
        Postgres on it. Two default StorageClasses make any PVC that omits a
        class undefined
      Then explicit `longhorn-r3` / `longhorn-r2` StorageClasses in `config/`,
      the per-volume counts the [storage split](design.md#storage-split) calls for, so
      Phase 6 chooses per volume rather than inheriting a global default.
      Defining `longhorn-r3` now is not a contradiction of the paragraph above:
      nothing binds to it until Phase 6, and anything that does will read
      Degraded until Phase 7 joins the third node. Expected, and the reason
      Phase 6's critical volumes are the ones worth deferring if that noise
      matters more than the ordering.
      *Exit:* `kubectl -n longhorn-system get nodes.longhorn.io -o wide` shows
      each disk schedulable at the data disk's capacity — which is what actually
      proves step 2 worked, more than `df` does.
      — *[`controllers/longhorn.yaml`](../../../deploy/cluster/infrastructure/controllers/longhorn.yaml)
      and
      [`config/longhorn-storageclasses.yaml`](../../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml).
      Five things the bullets above didn't say. **`defaultReplicaCount` is not
      the value that matters, and on its own it would have shipped the exact
      failure it was written to prevent**: it governs volumes created outside a
      StorageClass, while the `longhorn` class the chart installs reads
      `persistence.defaultClassReplicaCount`, whose default is 3. That class is
      created unconditionally — `persistence.defaultClass: false` removes the
      *default* annotation, not the class — so setting only the setting leaves a
      three-replica class installed on a two-node cluster, permanently Degraded,
      which is the alarm noise `LONGHORN_REPLICA_COUNT` exists to avoid. Both
      values carry it. **Uninstalling this release destroys every volume, and
      two lines exist to keep an automated retry from doing it**: the chart's
      pre-delete hook runs `longhorn-manager uninstall --force` — its own flag
      help is "uninstall even if volumes are in use" — and the CRDs carry no
      `resource-policy: keep`, so a Helm uninstall is total. helm-controller's
      install remediation has no strategy *but* uninstall (unlike upgrade, which
      can roll back), and that hook's `activeDeadlineSeconds: 900` is 1.5× the
      whole layer's budget, so this is the one release in the tree with
      `retries: 0`, and `strategy: rollback` is written out on the upgrade side
      where the alternative value is `uninstall`. The same hazard is what
      `prune: true` means here: deleting the file is deleting the data.
      **`storageReservedPercentageForDefaultDisk` had to be lowered for the
      *Exit* line above to be honest** — Longhorn reserves 30% by default
      because its default data path is normally the root filesystem, which 3b.2
      deliberately made untrue, and the number is multiplied into a fixed byte
      count when the node's default disk is created and stamped into its
      DiskSpec, so it is a before-first-registration decision, not a setting to
      revisit. 10, with the live guard left to `storageMinimalAvailablePercentage`.
      **Longhorn swallows a bad setting**: no `values.schema.json` in the chart
      and, underneath it, a manager that logs and skips a value that fails to
      parse or falls out of range — so a wrong replica count is not a failed
      install, it is Longhorn running on 3. `kubectl -n longhorn-system get
      settings.longhorn.io default-replica-count -o jsonpath='{.value}'` is the
      read-back, and 3b.13 is where it belongs — reading `{"v1":"2","v2":"2"}`,
      not `2`, because 1.11 holds the data-engine-specific settings as one value
      per engine and expands the scalar written in the chart into both. Compare
      it to `2` by hand and the answer is backwards. (One correction to 3a.6 while
      reading the chart: its declared floor is `kubeVersion: '>= 1.25.0-0'`, not
      the ≥ 1.34 the table asserts — that figure is Longhorn's release-note
      recommendation. The k3s pin satisfies both.) And **the two StorageClasses
      are literals on purpose, because a StorageClass cannot be edited** —
      Kubernetes rejects updates to `parameters`, `provisioner`, `reclaimPolicy`
      and `volumeBindingMode`, and a bound volume keeps the count it was created
      with regardless. So Phase 7 raises the ConfigMap variable and nothing in
      `deploy/` moves; `longhorn-r3`'s volumes rebuild their third replica on
      their own when the node joins. Both classes take `reclaimPolicy: Retain`
      over the chart's `Delete`, which is not the axis their names describe: with
      `prune: true` everywhere, a mistaken commit deletes a PVC as easily as a
      mistaken `kubectl`, and until Phase 8 that would be the data with it.*
- [x] **12. CloudNativePG operator** — pinned `HelmRelease`, no credentials, no
      configuration. Last because nothing else waits on it and Phase 4 is what
      makes it do anything.
      *Exit:* `kubectl get crd clusters.postgresql.cnpg.io`.
      — *[`controllers/cloudnative-pg.yaml`](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml),
      and the only component in layer 1 with no companion under `config/` —
      the instances of these CRDs are Phase 4's, not this phase's. Four things
      the bullet didn't say. **`config.clusterWide` is written out although it
      is already the default, because the other value fails silently and Phase 4
      is where it would surface**: `false` makes the chart inject
      `WATCH_NAMESPACE: cnpg-system` and demote the operator's common rules from
      a `ClusterRole` to a namespaced `Role`, so a `Cluster` created in the
      application's namespace is accepted by the API server, admitted by the
      webhook, and then never reconciled — no error, no event, no pods, just an
      empty status, which reads as a broken operator rather than as a scope
      setting. **`monitoring.podMonitorEnabled` is written out for the opposite
      reason — it is the one value here that cannot be flipped yet.** The
      template renders a `monitoring.coreos.com/v1` `PodMonitor`, nothing
      registers that type until Phase 6 installs the Prometheus operator, and an
      unknown kind is a failed Helm install — which under `wait: true` does not
      fail alone, it holds every other component in this directory and
      `infra-config` behind them. The queries it would scrape are installed
      regardless: the chart plants ~480 lines of them in
      `cnpg-default-monitoring` and every `Cluster` inherits them unless it sets
      `disableDefaultQueries`, so Phase 6 gains a scrape, not a metric.
      **`retries: 1` here is the inverse of 3b.11's `retries: 0`, and for a
      reason worth stating next to it**: helm-controller still remediates a
      failed install by uninstalling, but all eleven CNPG CRDs carry
      `helm.sh/resource-policy: keep`, so the uninstall skips them and every
      `Cluster` and `Backup` survives it — and because they keep the ownership
      metadata Helm stamped on them, the reinstall adopts them instead of
      colliding. The blast radius of a retry is the operator Deployment, and
      losing that costs reconciliation (no failover, no scheduled backup) rather
      than data; running Postgres pods keep serving. And **the chart ships a
      `values.schema.json`, which is less than it sounds** — it sets no
      `additionalProperties: false` anywhere, so a misspelled key is still
      accepted and still silently unread, exactly as in 3b.8 and 3b.11. What it
      does catch is a wrong *type*, which is the mistake `postBuild`
      substitution makes easy elsewhere in this tree. (One correction to 3a.6,
      the same one 3b.11 records: the chart's declared floor is
      `kubeVersion: '>=1.29.0-0'`, not the 1.34–1.36 in the table — that range
      is the operator's own supported-Kubernetes matrix, which for a database
      operator is the number that matters. The k3s pin satisfies both.)*
- [x] **13. Phase gate as a command** — `scripts/k3s/Test-ClusterPlatform.ps1`,
      asserting every *Exit* above in one run, in the same verification-stage
      shape as the other scripts. "Phase 3 is done" should be something that
      exits 0, not something remembered — and it doubles as the smoke test after
      a node rebuild.
      Run the [portability check](design.md#verification) over `deploy/` before ticking:
      grep the new tree for the base domain, any LAN address, and the VIP. Every
      one of them should appear as a `${...}` substitution and nowhere else.
      — *[`scripts/k3s/Test-ClusterPlatform.ps1`](../../../scripts/k3s/Test-ClusterPlatform.ps1),
      wrapped by
      [`verify-cluster-platform.yml`](../../../.github/workflows/verify-cluster-platform.yml)
      — deliberately **not** "Provision 6": every Provision workflow changes the
      cluster and this one writes nothing, so numbering it into that sequence
      would misdescribe it. **Unticked until its first green run**, for the same
      reason as 3b.10: the script is committed and the portability grep is clean,
      but a gate that has never exited 0 has not proved anything. Five things the
      bullets above didn't say. **It does not stop at the first failure, and that
      is the one place it departs from every other script here.** The Provision
      scripts throw immediately and are right to — their next action writes to a
      disk or to etcd, so continuing past a surprise is how a wrong assumption
      becomes a wrong filesystem. Nothing here writes anything, and a gate that
      stops at the first failure costs one dispatch per problem, which across
      twelve steps is how a bad afternoon becomes a bad week. Its counterpart
      rule: a check that cannot be **evaluated** is a failure, never a skip —
      an absent object and an unreachable node both mean *not proven*, and an
      exit code that treats those as anything else is satisfiable by a cluster
      that is switched off. **Where a check is made from is part of the check.**
      3b.8, 3b.9 and 3b.10's exit criteria are evaluated from the runner over the
      LAN — one raw TLS handshake to `${INGRESS_VIP}:443` carrying
      `home.${DOMAIN}` as SNI, which is what `openssl s_client -servername` does
      and what no DNS record resolves until Phase 7 — because a node asked
      whether the VIP answers can say yes about its own loopback while every LAN
      client sees nothing, which is precisely the failure 3b.9 spent a day on.
      For the same reason 3b.2's mount is checked on **every node the cluster
      reports**, not on the one dispatched against: a mounted disk is not shared
      through etcd, so asking one node has proven one third of the property.
      **It takes no repository variables at all.** Everything it compares against
      comes from the cluster's own `aerie-cluster-config` and from the two
      committed maps, so adding a key to
      [`cluster-config.json`](../../../scripts/k3s/cluster-config.json) or a `kubernetes`
      block to [`parameters.json`](../../../scripts/secrets/parameters.json) makes this
      gate require it with no edit here — the same pointer-half discipline 3b.1
      and 3b.6 already lean on. **Several checks assert the trap rather than the
      happy path**, which is what makes them worth a script: that both of the
      `ClusterSecretStore`'s `secretRef`s carry a namespace (without it ESO
      skips validation entirely and reports `Ready=True` over a store that has
      never read a credential — 3b.5's exit criterion is satisfiable by a store
      that does nothing); that cert-manager's two DNS-01 flags are on the
      running container's **command line**, not merely in the manifest, since a
      value the chart never read is indistinguishable from the split-horizon
      failure it was set to fix; that exactly one `TLSStore` is named `default`
      cluster-wide, since Traefik deletes the default store outright on finding
      two and puts every hostname silently back on its self-signed certificate;
      that the Traefik Service carries `kube-vip.io/loadbalancerIPs` rather than
      the deprecated field; that Longhorn's **read-back** `default-replica-count`
      and the `longhorn` class's own `numberOfReplicas` both match, since
      Longhorn logs and skips a setting it cannot parse; and that no Flux object
      is `suspend`ed, which is how an object reports Ready about a
      reconciliation that stopped weeks ago. And **the portability check splits
      in two, because its halves need different things.** The repo-only half is
      in [`ci.yml`](../../../.github/workflows/ci.yml) and runs on every PR: no address
      literal anywhere under `deploy/` (bar the two public DNS-01 resolvers),
      and every `${TOKEN}` in the **built** output — comments stripped, so the
      ones in `infrastructure.yaml` explaining this hazard don't trip it — is a
      key `cluster-config.json` declares, which is the only thing that catches
      `${DOMIAN}`, an undefined token being an empty string rather than an
      error. The half that greps for the base domain and the VIP themselves
      cannot live there at all: [ethos](../../ethos.md) keeps those values out of
      the repository, so the gate is the only place the tree and the values are
      both present.*
