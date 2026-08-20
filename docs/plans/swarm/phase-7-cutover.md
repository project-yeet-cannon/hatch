[← Phase 6](phase-6-observability.md) · [Design & decisions](design.md) · [Phase 8 →](phase-8-backup-v2.md)

---

# Phase 7 — Cutover

**Status: In progress — 7a underway**

> Re-scoped twice. The first pass turned five bullets into an **order**, and
> found six things that either lose data, lose the rollback, or stop the phase
> dead partway. This second pass takes three facts about *this* installation
> seriously and deletes the machinery those three facts make unnecessary:
>
> - **Nobody depends on this yet.** The house is being stood up for the first
>   time. Downtime is paid out of the operator's own evening rather than out of
>   anyone's trust, which means its length is no longer a design constraint.
> - **The restore already in the cluster is the one we cut over to.** 4b.9's
>   Job has been run for real, against a real dump. Aerie's data is almost
>   entirely *derived* from Home Assistant, which has not stopped producing it,
>   so being up to the minute is not what makes the restore correct.
> - **The participating devices can be power-cycled.** There is a handful of
>   them and they are all in this house. A stale DNS cache is a reboot, not a
>   propagation problem.
>
> Together those delete: the measured outage window and the rehearsal that timed
> it, the announcement to the house, the DNS TTL lowered an hour ahead, the
> per-client Unbound view for devices that could not be handed a hosts file,
> and the freeze → dump → restore → compare sequence the entire middle of this
> phase used to be arranged around. Seventeen scripted steps become fourteen,
> and the cutover proper is five of them with no clock on any: **stop the old
> stack → take the final dump → flip DNS → restart the clients → look at
> everything.**
>
> One thing the simplification costs, named here rather than discovered later:
> **any write made on the old host since that restore is not in the cluster.**
> Sensor history refills itself from Home Assistant; things a person typed — a
> storage location, a printed label — do not. 7b.3 and 7b.4 keep the escape
> hatch open (a fresh dump and a second run of the same Job, at leisure, with
> the site already down), which is a cheap option to hold rather than a step
> anyone has to perform.
>
> Five findings survive the re-scope, because none of them were ever about the
> window. The first has lost half its content and kept the half that matters.
>
> - **Long-lived connections never consult DNS.** The family shell's SSE
>   streams, held open for hours, keep talking to the old Caddy until something
>   closes them — no matter what the resolver says. Stopping the old stack is
>   the only thing that closes them, which is why 7b.2 *stops* it rather than
>   merely bypassing it, and why the stop now comes **before** the flip instead
>   of a soak-length after it. The client-cache half of this finding is handled
>   by rebooting the clients (7b.5).
> - **Building the external vSwitch on the old box moves the host's own
>   address.** `New-VMSwitch -AllowManagementOS $true` relocates the host's
>   traffic onto a `vEthernet (ExternalSwitch)` adapter carrying a **new MAC**
>   ([Phase 1](phase-1-node-substrate.md)'s note, from the other side). The
>   pfSense reservation [6a.2](phase-6-observability.md) made against this box's
>   physical NIC stops matching, the host takes a pool address, and
>   `WINDOWS_EXPORTER_TARGETS` — a `staticConfigs` list, discovered by nothing —
>   starts pointing at an address that answers for someone else. It presents as
>   a firing `up == 0` on a host that is visibly running (7b.9).
> - **Flipping `whenUnsatisfiable` to `DoNotSchedule` deadlocks the api
>   rollout.** [5b.11](phase-5-app-tier.md) left a note saying a third node
>   makes `DoNotSchedule` free. It makes it *satisfiable*, which is not the same
>   thing: with `replicas: 3` on three nodes and `maxSkew: 1`, the Deployment's
>   default strategy (`maxSurge: 1`, `maxUnavailable: 0` — 25% of 3 rounds up
>   and down respectively) has to place a **fourth** pod before it may remove
>   any, and a fourth pod is skew 2 on every node. The surge pod sits Pending
>   and the rollout never completes — on the next image-automation bump, not on
>   the commit that made the change. The flip comes with an explicit
>   `maxSurge: 0` / `maxUnavailable: 1` strategy or it does not happen (7b.12).
> - **The local half of the backup dies with the old box.** `E:/restic-repo`
>   ([compose.backup.yml](../../../compose.backup.yml)) is one of
>   [Phase 0](phase-0-backup-and-dr.md)'s two repos, and it is on a disk this
>   phase reformats for Longhorn. Everything else about backup coverage narrows
>   at the same moment: `backup.sh` stops running when its `db` stops running,
>   so from 7b.2 until Phase 8 lands the only thing being backed up at all is
>   Postgres, by CNPG's own WAL archiving and `ScheduledBackup` (4b.10). Two
>   consequences are handled here rather than deferred: the repo is **copied
>   off the box before it is wiped**, and the final dump is **tagged** so Phase
>   8's `restic forget` cannot prune the last consistent copy of the old world
>   out from under a rollback (7b.3, 7b.9).
> - **Phase 5 handed over a deletion that breaks local development.**
>   [5b.13's handover](phase-5-app-tier.md#additions-this-phase-makes-to-other-phases)
>   lists "the connection strings in `appsettings.Docker.json`" among Phase 7's
>   deletions. They cannot go: the root [`compose.yaml`](../../../compose.yaml) —
>   the local dev stack, which this phase does not touch — runs the api with
>   `DOTNET_ENVIRONMENT=Docker` against a `db` container built from
>   [`containers/aerie-db/`](../../../containers/aerie-db/), and those two lines
>   are how it connects. `containers/aerie-db/` survives for the same reason.
>   The deletion list in 7b.8 is the checked version of that handover, not a
>   copy of it.
>
> Three decisions, unchanged in substance and cheaper than they were. **The
> cutover and the rebuild are still two sittings with a soak between them** —
> the old box, powered off and untouched, *is* the rollback, and wiping it the
> same night converts every problem found on day two into a restore-from-S3
> instead of a `docker compose start`. What has changed is the price: the soak
> costs two-node etcd, and two-node etcd on a cluster nobody depends on yet
> costs nothing worth naming. So the soak is as long as it is useful and as
> short as you like (7a.4), rather than the 48–72 hours a family's weekday used
> to argue for. **The house share has no data to move.** `share.${DOMAIN}` and
> the media library both mount the same external `//server/share` device from
> both stacks ([compose.share.yml](../../../compose.share.yml), 5b.7) — so the
> dufs in front of it blinks for the same few minutes as everything else, there
> is no copy to make, and there is no window in which the two stacks can
> diverge. **Phase 7 does not close the apiserver-VIP gap.**
> [Phase 2](phase-2-k3s-flux-secrets.md) says the kubeconfig points at node 1
> "until Phase 7 restores a third node", which reads like a fix and is not one:
> three nodes restore *quorum tolerance*, and the kubeconfig still names one
> machine. Losing node 1 is still a manual repoint. Say so in that doc rather
> than letting the sentence expire quietly.

---

## The outage

There is no window to announce and no stopwatch to run. The site is down from
the moment the old stack stops (7b.2) until the clients have picked up the new
answer (7b.5) — a few minutes if you reboot them, up to an hour for anything
left to expire Unbound's default 3600-second TTL on its own. Both are
acceptable, which is the whole point of the re-scope. Take the evening.

The only sequencing that still matters is the one the first finding names: the
old stack is stopped **before** DNS moves, not after. A flip that leaves the
old Caddy answering leaves every open SSE stream pinned to it.

What is **not** affected, and is worth knowing so it is not diagnosed as
collateral:

- the **data** on the house share and the media library — the device is
  external to both stacks, and only the dufs in front of it is in the outage
- Home Assistant, and the alert path through it — Aerie reaches HA outbound
- Tailscale — the subnet routers are on the hosts (5b.13), not in the stack
- the cluster's own Kuma monitors, which check
  [in-cluster Service DNS](../../../deploy/cluster/observability/controllers/static-monitors-configmap.yaml)
  and never resolved a public hostname to begin with
- the cluster's `status`, `logs` and `metrics` — they have been serving over
  `--resolve` since Phase 6 and are unaffected throughout. It is the **old
  host's** copies of those three that stop at 7b.2, and they are what is being
  replaced

The old host's own Kuma, Prometheus and Alertmanager are *in* the stack, so
they stop alongside what they were watching rather than alarming about it —
which is one more thing the simplified order deletes rather than manages.
7b.1 covers the seconds before they go.

---

## Phase 7a — Manual prerequisites

*Six one-time steps, none of them code. 7a used to exist because every question
still open at the start of the outage got asked with the site down. That is no
longer the expensive failure — an evening is cheap. What is still expensive is
**irreversibility**: 7b.9 reformats the disk that holds the old world. So 7a is
now about arriving at the rebuild with nothing left to find out, and the
cutover itself is allowed to be scrappy.*

### [ ] 1. Confirm Phases 3–6 actually landed

Not "the boxes are ticked" — dispatch all four gates and get four green runs:

- [x] [`verify-cluster-platform.yml`](../../../.github/workflows/verify-cluster-platform.yml),
- [x] [`verify-data-tier.yml`](../../../.github/workflows/verify-data-tier.yml),
- [x] [`verify-app-tier.yml`](../../../.github/workflows/verify-app-tier.yml),
- [ ] `verify-observability.yml` (6b.15). Four, in that order, because each phase's
gate assumes the one below it.

Two results in that output are load-bearing here and worth reading rather than
skimming past: the app tier's assertion that **all four Ingresses answer 200
over the VIP with a production certificate** is the thing 7b.5 is about to point
the whole house at, and the data tier's assertion that **the newest CNPG
`Backup` is completed and younger than the schedule interval** is the only
backup this phase leaves standing after 7b.2.

### [x] 2. Inspect the ecosystem, with your own hands, before anything is irreversible

The gates prove properties. They do not tell you whether the family shell
*feels* right, whether a dashboard panel is empty, or whether the kiosk's
dashboard renders on the actual tablet. This is the step that does, and
it is deliberately the longest one in 7a.

Two ways in, and a third that no longer exists. **Start at the top and stop as
soon as it answers your question.**

*Tier 1 — one command, no state changed anywhere.* The
`--resolve` path [5a.7](phase-5-app-tier.md) and
[6a.6](phase-6-observability.md) established. Correct for a spot check, and the
only tier that is safe to use from a machine you do not own:

```sh
for h in home kiosk files share status logs metrics; do
  printf '%-8s ' "$h"
  curl -s -o /dev/null -w '%{http_code} %{ssl_verify_result}\n' \
    --resolve "$h.${DOMAIN}:443:${INGRESS_VIP}" "https://$h.${DOMAIN}/"
done
```

Seven lines, each `200 0` (`share` answers `401`, which is
[correct](../../../charts/aerie/values.yaml) — dufs gates the path, not just
writes). What it cannot do is drive a browser: `curl --resolve` does not help
Chrome, and the family shell is a single-page app whose behaviour is the thing
being inspected.

*Tier 2 — a hosts file, on one machine you own.* This is the tier to actually
spend an hour in. Seven lines in `/etc/hosts` (or
`C:\Windows\System32\drivers\etc\hosts`), pointing every name at the VIP:

```text
${INGRESS_VIP}  home.${DOMAIN} kiosk.${DOMAIN} files.${DOMAIN} share.${DOMAIN} status.${DOMAIN} logs.${DOMAIN} metrics.${DOMAIN}
```

Full fidelity — real browser, real TLS chain, real SPA, real logins. The
wildcard certificate is genuine for these names, so there is nothing to click
through. **Your machine only.** The warning 5a.7 and 6a.6 both carry is not
about tidiness: a hosts entry on a machine someone else uses is an outage they
cannot diagnose, sitting behind a file they do not know exists, surviving the
rollback in 7a.6 that fixes everyone else. Remove the lines when you are done,
before 7b.5 rather than after — a stale hosts file that happens to be correct
today is the one that breaks the day the VIP moves.

*There is no Tier 3.* This is where a per-client Unbound view used to go — a
way to hand the kiosk tablet, a phone or a Sonos speaker the new address
without touching the rest of the house, since none of them can be given a hosts
file. It was the riskiest thing in 7a (a custom-options block Unbound rejects
is a resolver that does not start, for everyone at once), and it existed only
because the flip had to be right the first time.

It does not have to be. **Inspect those devices after the flip instead** —
7b.6. If one of them is unhappy, reverting the flip is one line in the same box
and a reboot of the handful of devices that took the new answer. Trading a
resolver outage for the whole house against an evening of your own is not a
trade worth making twice.

**What to actually inspect**, once you are in:

- **`home`** — log in, walk the family apps, open the storage helper and print a
  QR label. `Apps__PublicBaseUrl` is `https://home.${DOMAIN}` in the chart, and
  labels outlive the laptop that printed them — a label produced here must be
  scannable after the cutover, which is the point of it being canonical.
- **`kiosk`** — on the tablet, not a desktop window sized to look like one;
  after the flip (7b.6), since the tablet cannot be given a hosts file. The
  `/` rewrite to `/apps/dashboard/` is Ingress-level (5b.9) and the compose
  version was Caddy-level; this is where a difference shows.
- **`files`** — `/version.json` and the APK itself, and then the update path
  end to end if the tablet will let you: this is what every kiosk in the house
  checks on a timer.
- **`share`** — an upload and a delete through dufs, then confirm the file is
  on the real share from a Windows box. Both stacks mount the same device, so a
  file written here appears on the old host too; that is the confirmation, not
  a coincidence.
- **the media library** — play something on a Sonos speaker; also after the
  flip (7b.6). It is the only consumer of the read-only mount, it cannot log
  in, and it is the client most likely to have an opinion about a certificate
  or a redirect.
- **`status`** — every monitor green, and the Home Assistant notification
  provider present and *tested* from Kuma's own UI (6b.13 created it; a
  provider that exists and has never fired is not a path).
- **`logs`** — the index pattern resolves, and lines are arriving from all
  three sources this phase depends on: cluster pods, the VM console shipper
  (`/api/vm-console-logs`), and the kiosk (`/api/kiosk-logs`). The last two go
  through the api, so they are also a live proof the ingress path works for
  something that is not a browser.
- **`metrics`** — Grafana's four dashboards render with data, Prometheus's
  targets page has no permanent `down` other than the ones 6b.5 turned off, and
  Alertmanager shows the Watchdog route firing into Kuma (6b.8).
- **the alert path, deliberately** — fire one real alert (silence it after) and
  confirm it arrives on a phone. Every other check in this list proves
  something is up; this is the only one that proves you will be *told* when
  something is not.

Anything this step finds gets fixed **now**, in the phase that owns it — not
carried into 7b as a known issue. A defect found here costs a commit and a
reconcile. The same defect found after 7b.9 has reformatted `E:` costs a DR
restore.

### [x] 3. Prove the new delivery path before deleting the old one

7b.8 deletes [`cd.yml`](../../../.github/workflows/cd.yml), and after that the
only way a code change reaches production is 5b.12's image automation. That
path has been running alongside a deploy that also worked, which means a silent
failure in it is invisible. Make it visible while there is still a fallback:

push a trivial commit, then follow it all the way — [publish.yml](../../../.github/workflows/publish.yml)
emits the `<timestamp>-<sha>` tag, the `ImagePolicy` selects it, the
`ImageUpdateAutomation` commits to the **site repo**, the `aerie-site`
`GitRepository` reconciles, `aerie-image-tags` carries the new value, and the
`api` Deployment rolls. Watch the pods actually cycle and confirm the change is
live over Tier 1 or 2 above.

Do it twice if the first is the first one that has ever run — the interesting
failure is the second bump, where a stale ConfigMap or an unsorted tag shows up
as "nothing happened" rather than as an error.

### [x] 4. Decide how long the soak is, and write the end date down

The soak is the deliberate gap between the cutover (7b.1–7b.8) and the rebuild
(7b.9 onwards), and it is the last stretch during which rollback is a
`docker compose start` rather than a restore from S3.

It used to be 48–72 hours, argued from "one ordinary weekday's use by people
who are not you". There is no such weekday yet, so the argument that remains is
the smaller one: **one overnight** (the hourly and nightly jobs, the staggered
reboots, anything that only runs when nobody is watching) and **one completed
CNPG backup cycle**. Overnight-plus-a-morning is enough. A day or two more
costs little — [Phase 2](phase-2-k3s-flux-secrets.md) is explicit that two-node
etcd has worse availability than one node, and the soak is exactly that window,
but a cluster nobody depends on can afford a bad night.

What is *not* optional is the end date. A soak with no end is a two-node
cluster with a story attached, and the old box sitting powered-on and
half-retired is the thing that quietly becomes permanent.

No announcement is needed this time. If someone happens to be using the site
that evening, tell them; otherwise the only person the outage costs is you.

### [x] 5. Pre-stage everything the old box's rebuild needs

All of this is discoverable during 7b.9 and all of it is much cheaper now,
because each item is a thing that has to be true *before* a workflow will run
and none of them are in the workflow's control:

  1. **A `hyperv-host-N` label on that box's runner.** Provision 0/1/5 dispatch
     by label ([`provision-1-install-k3s.yml`](../../../.github/workflows/provision-1-install-k3s.yml)
     offers `hyperv-host-0|1|2`); the old box's runner today has only
     `legacy-deployer`. Add the free label — do not remove `legacy-deployer`
     until 7b.8 has retargeted
     [`stagger-update-reboots.yml`](../../../.github/workflows/stagger-update-reboots.yml),
     whose `plan` job still names it as "the one runner guaranteed to exist".
  2. **The external vSwitch, and the reservation that follows it.** Creating it
     is Phase 1's one manual step, and per the third finding above it moves the
     host's own MAC. Plan for both halves in one sitting: create the switch,
     read the new MAC with `Get-NetAdapter -Name 'vEthernet (ExternalSwitch)' |
     Select-Object Name, MacAddress`, and re-reserve the host's existing address
     against it on pfSense. Do this *before* Provision 0 so the address in
     `WINDOWS_EXPORTER_TARGETS` never changes; if it does change anyway, the fix
     is a Provision 4 re-dispatch with the new value, not a Prometheus
     investigation.
  3. **A DHCP reservation for the new node VM's MAC**, exactly as Phase 1 did
     for the other two.
  4. **A decision about `E:`.** It holds the local restic repo. Copy it
     somewhere durable before the disk is reformatted — the house share is
     fine, it is a different machine — and keep it until Phase 8's cluster
     backup has taken and verified its own first snapshot.
  5. **Tailnet route approval on at least one other host.** Every host
     advertises the same LAN route under its own name (5b.13); confirm a
     *surviving* host's route is approved and working **before** the old box
     goes down, or remote access disappears for the length of the rebuild
     rather than for the length of a reboot. The old box's stale tailnet
     device — registered as `aerie` by `cd.yml`, before Provision 0 started
     naming hosts `aerie-hyperv-host-N` — gets removed from the admin console
     in 7b.13, not before: it is a working way in until it isn't.

### [x] 6. Know the rollback, and know when it expires

Written down before it is needed, because it is needed at the moment nobody
wants to be composing one:

| Found at | Rollback | Cost |
|---|---|---|
| 7b.2–7b.4 (before the flip) | `docker compose … start` on the old box | Nothing moved. The cluster holds a restore nobody is reading. |
| 7b.5–7b.7 (after the flip, during the soak) | Revert the one `local-data` line, `docker compose … start` on the old box, reboot the clients | Writes made against the cluster since the flip are stranded. Dump them out first if any matter. |
| After 7b.9 (box wiped) | Restore from S3 onto new hardware — [`docs/disaster-recovery.md`](../../disaster-recovery.md) | Hours, and it is a DR exercise, not a rollback. |

`start`, not `up -d`, in both of the first two rows. The old stack's containers
were created by [`cd.yml`](../../../.github/workflows/cd.yml) with an env block
that only exists inside that workflow; `up -d` re-evaluates the compose files
by hand, finds `${RESTIC_PASSWORD}` and friends empty, and **recreates the
containers without them**. `start` starts what is already there, with the
environment it was built with. This is the single most likely way to turn a
working rollback into a puzzle.

The middle row is the reason the soak exists, and the bottom row is the reason
it ends.

---

## Phase 7b — Scriptable, in this order

Step 1 is preparation that changes nothing anyone can see. **2–6 are the
cutover** — no clock on them, but in this order, and 2-before-5 is the one
piece of ordering that is not negotiable. 7 is the soak. 8 deletes the old path
from the repo. 9–12 are the third node and the settings it makes affordable,
13 is the paperwork, and 14 is the gate.

- [ ] **1. Take the old path out of your own hands, and silence the alerts you
      are about to trip on purpose.** Two chores, neither interesting, both
      noticed only by their absence.

      **Disable the Deploy workflow.** `cd.yml` fires on `workflow_run` from
      publish, so a merge to `main` at any point tonight redeploys a host you
      are in the middle of retiring — quietly, and after you have stopped it:

      ```sh
      gh workflow disable "Deploy"
      ```

      It is deleted outright in 7b.8; disabling it now is what keeps the next
      few hours legible.

      **Silence the cluster's Alertmanager, out far enough to cover
      7b.9–7b.11.** The rebuild fires `EtcdMemberDown`, `KubeNodeNotReady` and
      `LonghornVolumeDegraded` on purpose, and a phase that trains you to swipe
      those away is the failure mode
      [3b.11](phase-3-platform-services.md) named.

      **Silence those three by name, not the whole receiver** —
      `alertname=~"EtcdMemberDown|KubeNodeNotReady|LonghornVolumeDegraded"`.
      A blanket silence would still be in force at 7b.6, where the point of the
      step is to fire a real alert and watch it arrive on a phone; a silence
      that eats the one test of the alert path is worse than no silence at all.
      The cluster's Kuma needs nothing either way — it checks in-cluster
      Services and has no opinion about any of this.

      **The old host's alert path silences itself**, which is new: stopping the
      whole stack at once (7b.2) takes its Kuma, Prometheus and Alertmanager
      down with everything they were watching. The old plan left them running
      and had to gag them. All that is left is a few seconds of monitors going
      red as the stack winds down — silence Kuma first if you would rather not
      get the burst, but nothing here depends on it.
      *Exit:* a push to `main` produces no deploy, and a test alert against the
      cluster produces no notification. Both are undone in 7b.13, which is why
      that step is not optional.

- [ ] **2. Stop the old stack — all of it, and never `-v`.** From the old host,
      in the checkout `cd.yml` deploys from:

      ```sh
      docker compose -f compose.prod.yml -f compose.share.yml \
        -f compose.observability.yml -f compose.metrics.yml \
        -f compose.backup.yml stop
      ```

      This is the step the old plan spent five steps arranging around, and with
      no live-data requirement it is just a stop. It freezes the writes, it
      takes the site down, and — the part that actually matters — **it closes
      every long-lived connection**, which is the only way an SSE stream opened
      an hour ago will ever be told about the new address.

      `stop`, not `down`, and **never `docker compose down -v`.** The volumes
      are `pgdata`, `caddy_data`, `caddy_config` and `uptime_kuma_data`, and
      `pgdata` is the rollback. `stop` leaves the containers in place with the
      environment `cd.yml` created them with, which is what makes both the
      rollback in 7a.6 and the dump in 7b.3 a `start` rather than a
      reconstruction.

      Interpolation warnings from compose about unset `${...}` variables are
      expected and harmless here: `stop` and `start` act on containers that
      already exist, and nothing re-reads that env block.
      *Exit:* `docker compose ps` shows everything exited, `docker volume ls`
      still lists all four volumes, and `https://home.${DOMAIN}` fails to
      connect. Leave the machine powered on.

- [ ] **3. Take the final backup, and tag it so it cannot be pruned.** Two
      services, briefly, and nothing else:

      ```sh
      docker compose -f compose.prod.yml -f compose.share.yml \
        -f compose.observability.yml -f compose.metrics.yml \
        -f compose.backup.yml start db backup
      docker compose -f compose.prod.yml -f compose.share.yml \
        -f compose.observability.yml -f compose.metrics.yml \
        -f compose.backup.yml exec -T backup /app/scripts/backup.sh
      ```

      This dump is strictly better than the one the old plan took under a
      stopwatch: nothing is writing to `db`, and `uptime_kuma_data`'s SQLite is
      quiescent rather than live. It is post-4b.1, so it carries `aerie.dump`
      and `quartz.dump` in custom format alongside the dumpall — which is what
      makes 7b.4 possible at all.

      Then tag the snapshot in **both** repos, because
      `--keep-daily 7 --keep-weekly 4 --keep-monthly 12` will eventually reach
      it and Phase 8's CronJob inherits that policy against the same S3 repo:

      ```sh
      docker compose -f compose.prod.yml -f compose.backup.yml exec -T backup sh -c '
        for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
          restic -r "$REPO" tag --add cutover-final latest
        done'
      ```

      Inside the container, and `latest` rather than a short ID looked up with
      `jq` — [the image](../../../containers/backup/Dockerfile) has restic and
      sqlite but no jq, and the repo passwords only exist in that container's
      environment.

      Phase 8 gets a `--keep-tag cutover-final` on its `forget` — recorded in
      that phase's handover below, because a tag is worthless if the thing that
      prunes does not know about it.
      *Exit:* `restic snapshots --tag cutover-final` lists one snapshot in each
      repo, timestamped after 7b.2. **This is now the only complete copy of the
      old world**, and 7b.9 reformats the disk holding one of its two repos.

- [x] **4. IGNORE — put the last few hours into the cluster too.** Skip this
      unless the answer to "has anyone typed anything into the old site since
      the restore?" is yes. Sensor history is derived from Home Assistant and
      refills itself; storage locations, printed labels and anything else a
      person entered by hand do not.

      It is the same Job 4b.9 built to be run more than once, and the
      hand-created `aerie-pg-restore-restic` Secret it needs already exists —
      it was created for the run that has already happened:

      ```sh
      kubectl -n aerie create job --from=job/aerie-pg-restore aerie-pg-restore-cutover
      kubectl -n aerie logs -f job/aerie-pg-restore-cutover
      ```

      The Job pulls from **S3**, not the local repo — see the header of
      [`restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml) —
      so 7b.3's push having completed is a precondition, not a formality.

      Whether or not you re-ran it, spend two minutes comparing while the old
      `db` is still started, because this is the last cheap moment to:

      ```sh
      # old host
      docker compose -f compose.prod.yml exec -T db \
        psql -U user -d aerie -tAc 'SELECT count(*) FROM "EnvironmentReadings"'
      # cluster
      kubectl -n aerie exec -it aerie-pg-1 -- \
        psql -U postgres -d aerie -tAc 'SELECT count(*) FROM "EnvironmentReadings"'
      ```

      A mismatch is **expected** if you skipped the re-run — that is the gap
      the re-scope accepted, and its size is the useful thing to know. What
      would be a stop is a cluster count that is *wildly* off, or `"Devices"`
      missing rows a person would notice: that is a bad restore rather than a
      stale one, and it is worth finding now rather than after the old box is
      gone.

      Then stop the two services again:

      ```sh
      docker compose -f compose.prod.yml -f compose.share.yml \
        -f compose.observability.yml -f compose.metrics.yml \
        -f compose.backup.yml stop
      ```

- [ ] **5. Point pfSense Unbound at the VIP, then reboot the clients** — the
      one-line change this phase was originally described as. **Services → DNS
      Resolver → General Settings → Custom options**, where the existing pair
      ([reverse-proxy-architecture.md](../../reverse-proxy-architecture.md))
      is a `local-zone`/`local-data` redirect for the whole base domain:

      ```text
      server:local-zone: "${DOMAIN}." redirect
      server:local-data: "${DOMAIN}. IN A ${INGRESS_VIP}"
      ```

      One address changed, nothing else. Save; Unbound restarts and its own
      cache goes with it.

      What remains is client-side, and the old plan spent a whole pre-step an
      hour ahead of the window lowering the record's TTL to 60 so that tail
      would be short. **Don't.** The record keeps its default 3600, and the
      handful of devices that matter get power-cycled instead — the kiosk
      tablet, the Sonos speakers, whatever laptop or phone is in the house.
      That is a minute of scrappiness against a scheduled pre-step and a
      matching cleanup step, on an installation where an hour of a stale answer
      would have been survivable anyway. (`ipconfig /flushdns`,
      `sudo dscacheutil -flushcache`, or a Wi-Fi off/on will do for anything
      you would rather not reboot.)

      Verify in this order, because each answers a different question:

      ```sh
      dig +short A home.${DOMAIN} @<pfsense-lan-ip>   # the resolver itself
      dig +short A home.${DOMAIN}                     # a LAN client, through its cache
      ```

      Both return `${INGRESS_VIP}`. From a Tailscale client too — split DNS
      forwards `${DOMAIN}` to this same resolver
      ([tailscale-vpn-architecture.md](../../tailscale-vpn-architecture.md)), so
      it follows automatically, and confirming it is cheaper than assuming it.
      *Exit:* the seven hostnames resolve to the VIP from the LAN and from the
      tailnet.

- [ ] **6. Verify the seven hostnames without `--resolve`, and then the things
      that are not a browser** — the first time in the entire plan that the
      first half is possible, and the reason it is worth more than the
      identical-looking check in 5b.14:

      ```sh
      for h in home kiosk files share status logs metrics; do
        printf '%-8s ' "$h"
        curl -s -o /dev/null -w '%{http_code} %{remote_ip}\n' "https://$h.$DOMAIN/"
      done
      ```

      `%{remote_ip}` is the point: a 200 proves something answered, and only the
      address proves it was the cluster. Seven lines, seven `${INGRESS_VIP}`
      (`share` still 401s).

      Then the clients 7a.2 deliberately deferred to this moment, because there
      is no longer a Tier 3 to preview them through: the **kiosk tablet** (which
      has been showing an error page since 7b.2 and should recover on its own,
      or after a reboot), a **Sonos** speaker playing from the media library,
      and **Home Assistant** — trigger one real alert and confirm it lands on a
      phone. Every other check in this list proves something is up; that last
      one is the only one that proves you will be *told* when something is not.

      The loop takes ten seconds. This paragraph is the step.

- [ ] **7. Soak, on the schedule 7a.4 set.** Not a waiting step — a watching
      one. Each day of it, look at: `metrics` for a target that went down and
      stayed down, `logs` for a service that stopped shipping (an absence is
      invisible in a dashboard built to show volume), `status` for a monitor
      that flaps, and the CNPG `Backup` list for a completed backup younger than
      the schedule.

      Two things are true only during this window and worth holding in mind.
      **Rollback is still cheap** — 7a.6's middle row, and the old box is
      sitting there stopped rather than wiped. And **etcd is two of two**, so a
      single node lost is a cluster lost, which means: no maintenance, no
      reboots, no experiments on either node until 7b.10. If a Windows Update
      reboot is scheduled to land on one of the two hosts inside the soak
      ([Phase 1](phase-1-node-substrate.md)'s staggering), move it.

- [ ] **8. Delete the compose path from the repo** — one commit, after the soak
      passes, not before. Doing it during the soak would make the tree describe
      a system the rollback needs.

      Delete outright:

      - [`compose.prod.yml`](../../../compose.prod.yml),
        [`compose.share.yml`](../../../compose.share.yml),
        [`compose.observability.yml`](../../../compose.observability.yml),
        [`compose.metrics.yml`](../../../compose.metrics.yml),
        [`compose.backup.yml`](../../../compose.backup.yml)
      - [`.github/workflows/cd.yml`](../../../.github/workflows/cd.yml) — whole
        file, which 5b.13 spent a step making possible by moving the two host
        installs into Provision 0
      - [`containers/caddy/`](../../../containers/caddy/) and the `aerie-caddy`
        build/publish job in
        [publish.yml](../../../.github/workflows/publish.yml) — Traefik replaced
        it, and 6b.12 already deleted the monitor that watched it
      - the bind-mounted sources only the deleted compose files referenced, per
        [Phase 6's handover](phase-6-observability.md#additions-this-phase-makes-to-other-phases):
        [`containers/fluent-bit/`](../../../containers/fluent-bit/),
        [`containers/prometheus/`](../../../containers/prometheus/),
        [`containers/grafana/provisioning/`](../../../containers/grafana/provisioning/),
        [`containers/opensearch-provision/`](../../../containers/opensearch-provision/),
        [`containers/autokuma/static-monitors/`](../../../containers/autokuma/static-monitors/)

      Retarget rather than delete:

      - [`stagger-update-reboots.yml`](../../../.github/workflows/stagger-update-reboots.yml)'s
        `plan` job, whose `runs-on: [self-hosted, legacy-deployer]` and its
        comment both describe a runner that is about to stop existing. Any
        `hyperv-host-*` will do — the job only does arithmetic — and the comment
        needs rewriting rather than editing, since its reasoning ("the one
        runner guaranteed to exist regardless of Phase 1 progress") is no longer
        the reason.

      **Keep, against the Phase 5 handover's list:**

      - [`containers/aerie-db/`](../../../containers/aerie-db/) and the two
        connection strings in
        [`appsettings.Docker.json`](../../../src/Aerie.Api/appsettings.Docker.json) —
        finding 6. The root [`compose.yaml`](../../../compose.yaml) is the local
        dev stack and still uses both. The cluster reads the same file and
        overrides the strings with env vars (5b.5), so they are a dev default,
        not a production credential; a comment saying that is worth more here
        than a deletion.
      - [`containers/backup/`](../../../containers/backup/) — its image is what
        [`restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml)
        runs and what Phase 8's CronJob will run. Only `compose.backup.yml` goes.
      - [`containers/kuma-provision/`](../../../containers/kuma-provision/) —
        6b.13 turned it into a first-party image the cluster pulls.

      *Exit:* CI green; `grep -rn 'compose\.' .github/ docs/ scripts/` turns up
      only prose that has been updated to match; a push to `main` publishes
      images and rolls the cluster through image automation, with nothing
      deploying to anything.

---

- [ ] **9. Rebuild the old box as the third Hyper-V host** — everything
      7a.5 pre-staged, executed. In this order, because two of these are
      one-way:

      1. **Copy `E:/restic-repo` off the machine** and verify the copy with
         `restic -r <copy> check`. An unverified copy of a backup repo is the
         same category of object as an untested backup.
      2. **Confirm the S3 repo still holds the `cutover-final` tag** (7b.3).
         From here on it is the only copy of the old world that is not on a
         disk about to be reformatted.
      3. Remove Docker Desktop and enable the Hyper-V role. Reboot.
      4. **Create the external vSwitch, read the new
         `vEthernet (ExternalSwitch)` MAC, and fix the pfSense reservation** —
         finding 3, and the step whose omission is diagnosed as a Prometheus
         fault a week later. If the host's address changes anyway, update
         `WINDOWS_EXPORTER_TARGETS` and re-dispatch Provision 4.
      5. Dispatch **Provision 0: New node VM** at this host's runner. Same
         workflow, same script, same golden image as the other two — including
         the MAC spoofing, the fixed-size data VHDX, the ShutDown stop action
         and chrony that Phase 1 enumerates, and including the windows_exporter
         firewall exception and Tailscale converge that 5b.13 moved into it.
      6. Dispatch **Provision 5: Node storage** against the new VM, **before
         Provision 1**. Longhorn's DaemonSet lands on a node the instant it
         joins, and its default data path is `/var/lib/longhorn` on the root
         filesystem — a node that joins before its disk is mounted starts
         filling its OS disk with replica data, and the first symptom is disk
         pressure rather than a storage error.

      *Exit:* the VM answers on its reserved address, `lsblk` shows the data
      disk mounted at `/var/lib/longhorn` from `/etc/fstab`, the host answers
      `:9182` from another machine on the LAN, and its Tailscale device is
      present under its `aerie-hyperv-host-N` name.

- [ ] **10. Join it as the third k3s server** — Provision 1 with `-JoinServer`,
      the branch [`Install-K3sNode.ps1`](../../../scripts/k3s/Install-K3sNode.ps1)
      has carried since Phase 2 and that nothing has used yet:

      ```text
      server --server https://<node-1-ip>:6443 --disable servicelb --token <token>
      ```

      The node picks up 6b.1's two settings on the way in without anyone
      remembering them — `vm.max_map_count` as a sysctl drop-in and
      `etcd-expose-metrics: true` in `/etc/rancher/k3s/config.yaml` — because
      they live in the installer rather than in a one-time fix applied to the
      two nodes that existed when 6b.1 ran. That is the whole argument for
      having put them there, and this is the run that tests it.

      **Watch the join.** It is the moment etcd goes from a two-member cluster
      that tolerates nothing to a three-member one that tolerates one, and it
      passes through a state where a new member is registered and not yet
      caught up. Do it with a terminal open, not as the last thing before bed.
      *Exit:* `kubectl get nodes` shows three `Ready` control-plane nodes;
      `kubectl -n kube-system get pods -o wide` shows Longhorn's and
      fluent-bit's DaemonSets rolled out on the new one;
      `curl -s http://127.0.0.1:2381/metrics | head -1` answers on all three;
      and `etcd_server_has_leader` is 1 for three members in Prometheus rather
      than two.

- [ ] **11. Raise the two counts, and confirm the cluster acts on them** —
      repository variables plus one Provision 4 dispatch, **no commit**, which
      is the entire reason [3b.11](phase-3-platform-services.md) and
      [4b.7](phase-4-data-tier.md) put them in a ConfigMap:

      | Variable | From | To | Lands in |
      |---|---|---|---|
      | `LONGHORN_REPLICA_COUNT` | `2` | `3` | [`longhorn.yaml`](../../../deploy/cluster/infrastructure/controllers/longhorn.yaml)'s `defaultReplicaCount` |
      | `POSTGRES_INSTANCES` | `2` | `3` | [`cluster.yaml`](../../../deploy/cluster/data/cluster/cluster.yaml)'s `instances`, and [`alerts/cluster.yaml`](../../../deploy/cluster/observability/config/alerts/cluster.yaml)'s threshold |

      Dispatch with `preflight_only` first — it prints the diff without applying
      it, which is the useful thing to do against a cluster that is now serving
      the house. Then apply and `flux reconcile kustomization flux-system
      --with-source` rather than waiting out the interval.

      Then confirm each one *did* something, because two of the three effects
      are not what the original bullet implies:

      - **The third CNPG instance schedules**, rather than sitting Pending.
        `podAntiAffinityType: required` (4b.6) is what makes this a real test of
        the new node rather than a number changing: with two nodes the third pod
        had nowhere to go, and the pattern on `POSTGRES_INSTANCES` caps at the
        node count for exactly that reason. Watch it bootstrap from the primary
        and join streaming replication.
      - **Longhorn rebuilds the `longhorn-r3` volumes to `Healthy`.** This is
        the first real proof the third node is carrying load, and it happens on
        its own: grafana, kuma and alertmanager have been `Degraded` since Phase
        6 because [`longhorn-storageclasses.yaml`](../../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml)
        asks for three replicas on a two-node cluster, exactly as that file said
        it would. Confirm each one reports three replicas on three distinct
        nodes, not merely `Healthy`.
      - **`LONGHORN_REPLICA_COUNT` itself changes nothing that exists today**,
        and knowing that prevents an hour of looking for an effect. Every
        Longhorn volume in the cluster names `longhorn-r2` or `longhorn-r3`
        explicitly, and a volume's replica count is fixed at creation; the
        variable is the default for a volume that names no class. Raising it is
        correct — it is what a future unclassed volume gets, and what
        [`Test-ClusterPlatform.ps1`](../../../scripts/k3s/Test-ClusterPlatform.ps1)
        asserts — but the visible change above comes from the node, not from it.

      *Exit:* `kubectl -n aerie get cluster aerie-pg` reports 3 instances,
      3 ready, on 3 distinct nodes; no Longhorn volume reports `Degraded`;
      `kubectl -n flux-system get cm aerie-cluster-config -o yaml` shows both
      values as `3`.

- [ ] **12. The three settings the third node makes affordable** — one commit,
      after 7b.11 is green rather than alongside it. Each of these was a
      deliberate two-node compromise with a note pointing here, and each is
      wrong to change before the node exists:

      **`synchronous.dataDurability: preferred` → `required`** in
      [`cluster.yaml`](../../../deploy/cluster/data/cluster/cluster.yaml). 4b.6
      called this the single most consequential line in that file: `preferred`
      degrades to asynchronous replication when the synchronous standby is away,
      which with two instances and Phase 1's staggered reboots was a recurring
      self-inflicted write outage. With three instances and `number: 1`, one
      node rebooting no longer costs the quorum, and RPO=0 becomes affordable.
      Change the value **and the comment** — a `preferred` that survives with
      its "revisit in Phase 7" note attached reads as permanent, which is the
      failure mode 4b.6 was guarding against.

      **`whenUnsatisfiable: ScheduleAnyway` → `DoNotSchedule`** in
      [`api-deployment.yaml`](../../../charts/aerie/templates/api-deployment.yaml),
      **with an explicit update strategy in the same edit** — finding 4:

      ```yaml
      strategy:
        rollingUpdate:
          maxSurge: 0
          maxUnavailable: 1
      ```

      Without it, three replicas over three nodes at `maxSkew: 1` cannot roll:
      the default `maxSurge: 1` / `maxUnavailable: 0` requires a fourth pod
      before any may be removed, and a fourth pod is skew 2 wherever it lands.
      The failure surfaces at the *next image bump*, not at this commit, which
      is what makes it worth writing down rather than discovering.
      [`files-deployment.yaml`](../../../charts/aerie/templates/files-deployment.yaml)
      needs no such change and should not get one — 2 replicas over 3 nodes
      leaves a node free for the surge pod, which is why its `DoNotSchedule`
      was already safe on two nodes.

      **The alert text the third node makes wrong**, in
      [`alerts/cluster.yaml`](../../../deploy/cluster/observability/config/alerts/cluster.yaml).
      The CNPG instance-count rule needs nothing — 6b.8 templated it against
      `${POSTGRES_INSTANCES}` for precisely this moment — but two annotations
      now lie: `EtcdNoLeader`'s "quorum is 2 of 2 until Phase 7", and the file
      header's note that `LonghornVolumeDegraded` is *expected* to fire until
      Phase 7. Both were true and are now the opposite of true, and a degraded
      volume that reads as expected is an alert nobody acts on. While there,
      reconsider `for: 1m` on the two etcd rules: it was chosen because a lost
      member on two servers was an instant outage, and on three it is a warning
      with time to spare.
      *Exit:* `kubectl -n aerie get cluster aerie-pg -o jsonpath=…` reports
      `required`; a deliberate rollout (`kubectl rollout restart deploy/api`)
      completes rather than stalling with a Pending pod; `promtool check rules`
      passes on the edited file.

- [ ] **13. Put back everything 7b turned off, and fix the documents that now
      lie.** The unglamorous half, and the half whose omission is discovered
      during an incident.

      Re-enable: **expire the three-alert silence from 7b.1** rather than
      letting it lapse on its own, and confirm a test alert still arrives
      afterwards — a silence that outlives the thing it was covering is
      indistinguishable from an alerting system that works. The Deploy workflow
      disabled in that same step needs nothing, because 7b.8 deleted it
      outright; confirm instead that `gh workflow list` no longer shows it. The
      DNS record keeps its default TTL — the re-scope never lowered it, so
      there is nothing to put back there either.

      Remove: the stale `aerie` Tailscale device from the admin console (7a.5.5),
      now that the rebuilt host is on the tailnet under its own name; and any
      hosts-file entries from 7a.2's Tier 2, if that has not already happened.

      Rewrite, because these are now actively misleading rather than merely
      stale — the distinction that decides what belongs here versus in Phase 9:

      - [`docs/disaster-recovery.md`](../../disaster-recovery.md) — the document
        someone reads at 3am, currently describing a rebuild procedure for a
        machine that no longer exists. It cannot wait for Phase 8's backup
        rework: at minimum it must say, accurately, what is backed up today
        (Postgres, by CNPG to S3) and what is not (everything else, until Phase
        8), and how to restore the former.
      - [`docs/reverse-proxy-architecture.md`](../../reverse-proxy-architecture.md) —
        Caddy, container labels and the `edge` network are gone; the
        `local-zone`/`local-data` half is still exactly right and is what 7b.5
        changed. Rewrite the routing half around Traefik and Ingress objects.
      - [`docs/delivery-architecture.md`](../../delivery-architecture.md) — its
        "Path 1" and its "Where this is going" section both describe this phase
        in the future tense. There is one path now.
      - [`docs/tailscale-vpn-architecture.md`](../../tailscale-vpn-architecture.md) —
        one sentence: it is installed by Provision 0 on all three hosts, not by
        `cd.yml` on one.
      - [`phase-2-k3s-flux-secrets.md`](phase-2-k3s-flux-secrets.md)'s
        apiserver-VIP note, per the third decision above: three nodes did not
        close that gap.

      [`docs/metrics-architecture.md`](../../metrics-architecture.md) and
      [`docs/monitoring-alerting-architecture.md`](../../monitoring-alerting-architecture.md)
      are **not** on this list — Phase 6 already assigned them to Phase 9, as
      candidates for folding into `cluster-architecture.md` rather than patching.

- [ ] **14. Phase gate as a command** — `scripts/k3s/Test-Cutover.ps1`, wrapped
      by `.github/workflows/verify-cutover.yml`, in the exact shape 3b.13, 4b.11,
      5b.14 and 6b.15 established: read-only, **not** numbered into the Provision
      sequence, does not stop at the first failure, and a check it cannot
      evaluate is a failure rather than a skip.

      One thing distinguishes it from every gate before it, and it is the reason
      it exists as its own script rather than as four more assertions bolted onto
      the others: **it resolves names normally.** Every earlier gate reaches the
      cluster with `--resolve` or an SNI handshake against the VIP, because no
      DNS record pointed there. This one must not — the property under test is
      that the house's own resolver sends the house to the cluster, and a check
      that supplies the answer cannot see it.

      Assert, at minimum:

      - three nodes `Ready`, three healthy etcd members with a leader, and all
        three answering `:2381`
      - **each of the seven hostnames resolves to `${INGRESS_VIP}` through the
        LAN resolver** and answers over TLS with a production certificate —
        no `--resolve`, no `/etc/hosts`, and the resolved address asserted
        explicitly rather than inferred from a 200
      - `aerie-pg` is Ready with 3 instances on 3 distinct nodes, and
        `synchronous.dataDurability` is `required`
      - no Longhorn volume is `Degraded`, and every `longhorn-r3` volume has 3
        replicas on 3 distinct nodes
      - `LONGHORN_REPLICA_COUNT` and `POSTGRES_INSTANCES` are both `3` in the
        live `aerie-cluster-config`
      - the `api` Deployment carries `DoNotSchedule` **and** a `maxSurge: 0`
        strategy, and its pods are one per node
      - all three `WINDOWS_EXPORTER_TARGETS` are `up`, including the rebuilt
        host — the check that catches finding 3
      - the newest CNPG `Backup` is `completed` and younger than the schedule
        interval, which after 7b.2 is the only backup the system has
      - **the compose path is gone from the tree**: no `compose.prod.yml`,
        `compose.share.yml`, `compose.observability.yml`, `compose.metrics.yml`,
        `compose.backup.yml`, `cd.yml` or `containers/caddy/`. A half-finished
        deletion is this phase's characteristic failure, and it is a file
        existence check rather than a cluster one.
      - the old host's address appears nowhere under `deploy/`, `charts/`,
        `scripts/` or `.github/` — the [portability check](design.md#verification)
        with one specific value, since this is the phase that retires it

      *Exit:* the workflow exits 0. Leave this box unticked until it has, for
      the same reason every gate before it stayed unticked: a gate that has
      never passed has proved nothing.

---

## What this phase deletes, and what survives

```text
compose.prod.yml            # 7b.8, deleted
compose.share.yml           #   all five, with the host that ran them
compose.observability.yml
compose.metrics.yml
compose.backup.yml
compose.yaml                # SURVIVES - local dev, untouched by this phase

.github/workflows/
  cd.yml                    # 7b.8, deleted whole
  publish.yml               # 7b.8, minus the aerie-caddy job
  stagger-update-reboots.yml# 7b.8, plan job retargeted off legacy-deployer
  verify-cutover.yml        # 7b.14, new

containers/
  caddy/                    # 7b.8, deleted - Traefik replaced it
  fluent-bit/               # 7b.8, deleted - deploy/ holds the cluster copies
  prometheus/
  grafana/provisioning/
  opensearch-provision/
  autokuma/static-monitors/
  aerie-db/                 # SURVIVES - compose.yaml builds it for local dev
  backup/                   # SURVIVES - restore-job.yaml and Phase 8 run it
  kuma-provision/           # SURVIVES - 6b.13 made it a cluster image

charts/aerie/templates/
  api-deployment.yaml       # 7b.12, DoNotSchedule + explicit strategy

deploy/cluster/
  data/cluster/cluster.yaml # 7b.12, dataDurability: required
  observability/config/alerts/cluster.yaml   # 7b.12, two annotations

scripts/k3s/
  Test-Cutover.ps1          # 7b.14
```

Nothing else under `deploy/` changes. That is the shape of a cutover done
right: the tree that describes the cluster was finished in Phases 3–6, and this
phase is mostly deletions elsewhere plus two variables and three settings.

## What Phase 7 deliberately does not do

- **It does not close the apiserver-VIP gap.** Three nodes make losing one
  survivable; the kubeconfig still names node 1, and losing *that* one is still
  a manual repoint. 7b.13 corrects Phase 2's sentence rather than the situation.
- **It does not migrate the house share, or anything on it.** Both stacks mount
  the same external device; there was never a copy to make.
- **It does not replace the local restic repo.** 7b.9 copies it off and Phase 8
  owns the cluster-native replacement. The gap between them is named, not
  papered over.
- **It does not rewrite `docs/metrics-architecture.md` or
  `docs/monitoring-alerting-architecture.md`.** Phase 6 assigned both to Phase 9,
  to be folded into `cluster-architecture.md` rather than patched twice.
- **It does not delete `containers/aerie-db/` or the connection strings in
  `appsettings.Docker.json`**, against Phase 5's handover. Finding 6 — local dev
  still uses both.
- **It does not touch image automation.** 7a.3 proves it works while a fallback
  exists; the phase changes nothing about it.
- **It does not turn the old box into a worker rather than a server.** Three
  servers is what design.md's quorum argument asks for, and a mixed topology
  would be a new decision with no problem behind it.

## Additions this phase makes to other phases

Recorded here, to be written into the phases that own them:

- **Phase 8 inherits a `--keep-tag cutover-final` requirement.** 7b.3 tags the
  final pre-cutover snapshot in both repos; Phase 8's restic CronJob inherits
  Phase 0's `--keep-daily 7 --keep-weekly 4 --keep-monthly 12` against the same
  S3 repo, and without the keep-tag it will eventually prune the last consistent
  copy of the old world. One flag, on the `forget` — and worth stating in that
  phase's own text, since a tag whose pruner does not know about it is
  decoration.
- **Phase 8 inherits a narrowed backup, and should know the shape of it.**
  Between 7b.2 and Phase 8, the only thing backed up anywhere is Postgres, via
  CNPG's WAL archiving and `ScheduledBackup`. Kuma's SQLite, Grafana's database
  and the OpenSearch indices have no copy at all. That is an argument for Phase
  8 starting with the restic CronJob rather than with the Longhorn backup
  target, and for its **backup-age alert** — already called the most valuable
  alert that does not exist — being the first thing in it rather than the last.
- **Phase 8's DR rehearsal now has a real target.** 7b.13 leaves
  `docs/disaster-recovery.md` accurate but minimal; the quarterly rehearsal is
  what turns it back into a document someone can follow, and the first rehearsal
  after this phase is the one that finds what the rewrite missed.
- **Phase 9 inherits the apiserver VIP as an open question**, now that "restore
  a third node" has happened and has not answered it. The two ways out are the
  ones [`docs/secrets-architecture.md`](../../secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver)
  already names; picking one is a Phase 9 decision, and the site-repo split is
  the natural moment, since a kubeconfig is per-installation.
- **Phase 9's `cluster-architecture.md` has one fewer excuse.** After 7b.13 the
  four documents rewritten here describe one system rather than two, which is
  what makes consolidating them a merge rather than an archaeology exercise.
- **[`docs/family-apps-architecture.md`](../../family-apps-architecture.md)'s
  photos decision comes due.** It defers blob storage to "after the k3s
  cutover", pointing at this phase — not as work for it, but as the condition
  that unblocks it. Longhorn now exists; the deferral should be re-decided
  rather than left pointing at a phase that has closed.

