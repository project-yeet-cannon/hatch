# Hyper-V node provisioning

Repeatable creation of the Hyper-V Linux VMs [the cluster plan](../../docs/plans/swarm/design.md) needs in two
places: the Phase 0 scratch VM for the DR-restore gate, and the Phase 1 node
VMs (one per Windows host). Same VM shape both times — only the cloud-init
payload (`-ExtraPackages` / `-RunCmd`) differs.

## The GitHub Actions workflow is the way to run this

[`Initialize-AerieNode.ps1`](Initialize-AerieNode.ps1) is the entry point, and
it holds all the logic. **Actions → *Provision 0: New node VM* → Run
workflow** is how a node gets built — dispatched, not hand-typed, so every
run is an auditable record of who built which node when. This repo's goal is
infrastructure-as-code; a script that only runs by someone remoting into a
box isn't that.

Running the script by hand — elevated PowerShell on the Hyper-V host, same
parameters as the workflow inputs — still works and is documented below, but
it's a fallback for when the runner isn't registered yet or is unreachable,
not an equally-valid alternative. The workflow checks out this repo on the
target host and calls the same script with the same parameters; nothing
lives in the workflow that doesn't live in the script, which is what keeps
the fallback path from drifting out of sync with the primary one.

The scripts have **no dependencies outside this directory**. `robocopy` (or
just copy) `scripts\hyperv\` onto a host if you need the fallback path with
no repo clone, no runner, no network path back to GitHub.

## One-time host prerequisites

Neither flow automates these — they're per-host setup, done once, and two of
them can knock the host off the network if scripted carelessly.

1. **Hyper-V role enabled.**

2. **An external virtual switch.** `-SwitchType External` isn't a real option:
   Hyper-V infers "external" from binding a physical NIC, not from
   `-SwitchType` (whose `ValidateSet` is only `Internal`/`Private`, which is
   why passing `External` errors).

   ```powershell
   Get-NetAdapter -Physical | Where-Object Status -eq 'Up'   # find the NIC name
   New-VMSwitch -Name ExternalSwitch -NetAdapterName "<adapter name>" -AllowManagementOS $true
   ```

   `-AllowManagementOS $true` matters on these single-NIC home hosts — without
   it, creating the switch drops the host itself off the network. That is also
   exactly why this isn't scripted: on a remote session it would sever the
   session that was running it.

3. **The OpenSSH client**, for post-boot verification — **installed for you**
   by the workflow's *Ensure runner dependencies* step
   ([`scripts/runner/`](../runner/README.md)), so this is only a by-hand step
   when running the script outside Actions on a machine that lacks it:

   ```powershell
   Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
   # or, from a checkout, in an elevated shell:
   .\scripts\runner\Install-RunnerDependencies.ps1 -Dependency OpenSshClient
   ```

4. **An SSH keypair** (`ssh-keygen -t ed25519`). The public half is injected
   into the VM by cloud-init; the private half is used only from the host to
   verify the result, and is never written into the VM.

5. **A pfSense DHCP reservation per VM**, keyed to the MAC you're about to
   pass in. Do this *before* running — cloud-init needs DHCP during first
   boot, and the reservation is what makes the node's address predictable.

### Additionally, for flow B

1. **A self-hosted Actions runner on each host**, registered with a label
   matching the workflow's `host` choice (`hyperv-host-0` / `-1` / `-2` —
   rename them in [`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml)
   to whatever you actually use). The runner service must run as a **local
   Administrator**: the Hyper-V cmdlets and the scripts'
   `#Requires -RunAsAdministrator` both demand it, and membership in
   *Hyper-V Administrators* alone is not enough.

2. **Repository variables and secrets**, under Settings → Secrets and
   variables → Actions:

   | Name | Kind | Required | What |
   |---|---|---|---|
   | `NODE_SSH_PUBLIC_KEY` | variable | yes | contents of `id_ed25519.pub` |
   | `NTP_SERVER` | variable | yes | pfSense's LAN address |
   | `NODE_SSH_PRIVATE_KEY` | secret | unless `skip_wait` | contents of `id_ed25519` |
   | `DOMAIN` | variable | no | already set for `cd.yml`; sets the guest's FQDN |
   | `VM_LOG_SHIPPER_TOKEN` | secret | yes | shared secret for console-log shipping (below). Nothing issues it — mint one with [`scripts/secrets/new-shared-secret.sh`](../secrets/new-shared-secret.sh). This step degrades without it by skipping shipping for the VM, but that is degradation, not a supported configuration: [`parameters.json`](../secrets/parameters.json) marks it required and Provision 2 refuses to seed without it. |

   The qemu-img download is pinned in
   [`scripts/versions.json`](../versions.json), not by a variable — see
   [Golden image](#golden-image).

## MAC addresses

Made up, unique on the network, prefixed `00-15-5D` (Hyper-V's OUI). The last
three octets are used hierarchically: `[host]-[vm]-[nic]`. So host B's first
VM's first NIC is `00-15-5D-02-01-01`.

## On-disk layout

Everything Aerie puts on a host lives under one root on the data volume, so the
whole footprint is a single directory to find, back up, or delete:

```text
D:\aerie\
  vm-templates\                        -TemplatePath — golden VHDX, shared by every VM on the host
    debian-13-genericcloud.vhdx        dynamic, 100GB virtual — a file to copy, not a disk to run
    debian-13-genericcloud.vhdx.provenance.json
  VMs\                                 -VMStoragePath — Hyper-V config plus one directory per VM
    aerie-node-1\
      os-disk.vhdx                     -OsDiskPath — fixed 100GB, converted from the template
      data-disk.vhdx                   -DataDiskPath — fixed, unformatted; Longhorn claims it in Phase 3
      seed.iso                         NoCloud cloud-init seed for this VM
      console-log-shipper.ps1          only when console-log shipping is on - see below
```

**The rule: the OS disk goes on the host's fastest volume and is fixed; the
Longhorn data disk goes on its largest and is fixed; nothing Aerie creates on
a host is dynamic, and no Aerie VM has automatic checkpoints.** That last
clause belongs in the same sentence as the first three, because an automatic
checkpoint layers a dynamic differencing disk over a fixed one and makes them
untrue again — Hyper-V's default is on, and it is per-VM, so every VM has to
turn it off. `New-AerieVM.ps1` does, at creation.

The golden template is the one deliberate exception, and only because it is
never booted: it stays dynamic so it is ~2GB to copy between hosts rather than
100GB, and `New-AerieVM.ps1` converts it to fixed when it cuts a VM's OS disk
from it. Its provenance JSON records which it is.

Why this is a rule rather than a preference: every node built before it had
a *dynamic* OS disk on the slowest volume its host owned, sized 32 GB from a
template rather than from the workload. That put etcd's write-ahead-log fsync
p99 in the hundreds of milliseconds — one node at 1130 ms — against a healthy
target of single-digit ms, and left root filesystems at 80-85% and climbing.
Migrating all three onto fixed disks on measured-fast volumes put the entire
fsync distribution under 32 ms, with ~90% at or under 8 ms.

All three roots are defaults, not assumptions. `-VMStoragePath` (the
workflow's `vm_storage_path`) sets where both disks go; `-OsDiskPath` and
`-DataDiskPath` (`os_disk_path` / `data_disk_path`) override it one at a time,
which is what a host whose fastest volume is not its largest needs. Missing
directories are created on first run.

## Choosing the OS disk's volume — measure, don't infer

The rule above says "the host's fastest volume with room". Which one that is
has to be **measured per host**, and it is not the same answer on every
machine. Three nodes were migrated onto three different volumes, one of which
was not the boot volume.

Read per-physical-disk latency from `windows_exporter`, which every host
already runs (Provision 0 installs and converges it). Average over days rather
than minutes — a spot reading catches whatever the host was doing at the time:

```promql
rate(windows_logical_disk_write_latency_seconds_total[7d])
  / rate(windows_logical_disk_writes_total[7d])
```

(Read latency is the same pair with `read` for `write`. The exact metric names
track the `windows_exporter` version — if that query returns nothing, list what
this installation actually exposes with
`curl -s http://<host>:9182/metrics | grep logical_disk` and adjust. The shape
is always a latency-seconds counter over an operations counter.)

Then check free space on the candidate: a fixed OS disk needs its own size
**plus a 40 GB margin**, and `Move-NodeOsDisk.ps1` refuses a destination that
does not have it. A fixed VHDX must never be the reason a Windows boot volume
fills. On one of the three hosts the boot volume was the fastest by a wide
margin and still lost, on size alone.

**Two wrong readings are available here, and both were made.** The first
assumed the documented `D:\aerie\VMs` default without checking, and so
measured a volume the VM was not on — read the layout from the host, never
from this file's defaults. The second took "it is already on an SSD" at face
value and concluded no move was needed.

What settles it is a **comparison, not a datasheet**. Point the same question
at every volume on the host and compare the answers: identical workloads
measuring 6.8, 5.7 and 49 ms is a statement about the media, where any one of
those numbers alone is not. The claim worth holding to is not "SSDs are
faster" — it is that the fastest storage in a machine is usually idle while
the one latency-bound workload in the house is somewhere else.

### Maintenance on a live node: never merge a checkpoint chain

If a VM has an automatic checkpoint, its OS disk is an `.avhdx` differencing
disk and the chain has to be merged before anything can convert it. **Merge
only with the VM off**, which is what `Move-NodeOsDisk.ps1` does and why its
merge happens after the drain rather than in preflight.

That ordering was bought the hard way. Merging rewrites every block the
`.avhdx` holds. On a host whose volume backs both the node's OS disk and its
200 GB Longhorn disk, a merge against the *running* node saturated the volume
the guest was living on: 53% iowait, ext4 journal threads blocked in `D`
state, kubelet stalled, pods stuck `Terminating`, Longhorn's instance-manager
killed and its replicas rebuilding onto the same saturated disk. The load then
shifted to another node, which briefly reported `Ready=Unknown` — taking the
cluster to bare etcd quorum on what was supposed to be routine maintenance.

Nothing was lost, and the lesson is cheap to keep: with the VM off there is no
guest to starve, the node is already down for the conversion that follows, and
the merge is faster for having no concurrent writes.

## Flow B — GitHub Actions (primary)

Actions → **Provision 0: New node VM** → Run workflow. Pick the `host`
matching the runner label, fill in `vm_name`, `mac_address`, `expected_ip`,
and the per-host `memory_gb` / `os_disk_gb` / `data_disk_gb`. Set `os_disk_path`
on a host whose fastest volume isn't the one `vm_storage_path` points at — see
the rule under On-disk layout. `preflight_only` is available
as a checkbox and checks the host is ready — missing switch, wrong switch
type, a taken MAC, an occupied IP, a too-full volume — without building
anything.

The job writes a summary with the node's address, resources, and the full
post-boot report.

## Flow A — on the host (fallback)

Only when the runner isn't registered yet or is unreachable. Elevated
PowerShell, in `scripts\hyperv\`:

```powershell
.\Initialize-AerieNode.ps1 `
    -VMName aerie-node-1 `
    -MacAddress 00-15-5D-01-01-01 `
    -ExpectedIPAddress 10.0.0.21 `
    -NtpServer 10.0.0.1 `
    -Domain landis.family `
    -MemoryGB 16 `
    -DataDiskSizeGB 200 `
    -SshPublicKeyPath ~\.ssh\id_ed25519.pub `
    -SshPrivateKeyPath ~\.ssh\id_ed25519
```

Same `-PreflightOnly` switch, same effect as the workflow's checkbox above.

## What a run actually does

1. **Preflight.** Refuses to start unless the switch exists *and is External*,
   the MAC is unused, the VM name is free, the target IP doesn't already
   answer, the volume has room for a full template copy plus a fixed data
   disk, and the SSH keys are present and look right. Cheap failures before
   expensive ones.

2. **Golden image.** Builds it via `Get-GoldenImage.ps1` if this host doesn't
   have one; reuses it otherwise. See below.

3. **VM.** Delegates to `New-AerieVM.ps1`: copies the golden VHDX into a fresh
   per-VM OS disk (a full copy, not a differencing disk — so the template
   stays independently movable), creates the fixed Longhorn data disk, renders
   a NoCloud seed ISO with this VM's hostname / SSH key / NTP server, and
   creates a Generation 2 VM with Secure Boot on the
   `MicrosoftUEFICertificateAuthority` template (required for a shim-signed
   Linux guest), MAC spoofing on, static memory, autostart, `ShutDown` as the
   stop action, and Hyper-V's Time Synchronization integration service
   disabled so it can't fight the in-guest chrony config.

   The seed also sets a **break-glass console password** for the `aerie` user
   (`-ConsolePassword`, default `password`). `ssh_pwauth` stays `false`, so it
   is rejected over SSH and only works at the Hyper-V console — which already
   requires Administrator on the host. Without it, a VM that never reaches the
   network is unreachable by *any* means: cloud-init locks every account's
   password, so `vmconnect` offers a login prompt that nothing can satisfy and
   photographs of console scrollback become the only diagnostic. Pass
   `-ConsolePassword ''` to opt out and accept that.

   If `-LogIngestUrl`/`-LogIngestToken` are supplied (flow B sets these
   automatically from the `VM_LOG_SHIPPER_TOKEN` secret, which the table above
   requires; a VM built before it was set ships nothing until it's rebuilt or
   `-RecreateVM`'d), the VM's serial console (COM1, wired to a named pipe — Hyper-V has
   no way to redirect a COM port straight to a file) is also drained by a
   per-VM Scheduled Task (`Aerie-VMConsoleLog-<VMName>`, runs as SYSTEM,
   restarts on failure, survives host reboots) running
   `console-log-shipper.ps1`, which POSTs each line to Aerie.Api's
   `/api/vm-console-logs`. From there it flows through the same
   `fluent-bit -> OpenSearch` pipeline as every other Aerie log line, visible
   at `logs.<domain>` under service `vm-console.<VMName>`. `-RecreateVM`
   replaces this task along with the VM; there's no separate cleanup step for
   permanently decommissioning a node yet.

4. **Verify.** Waits out cloud-init *and the reboot cloud-init triggers on
   itself* — see below — then prints the node's identity, addresses, disks,
   chrony sources, and Hyper-V daemon state, and fails if the machine
   answering at `-ExpectedIPAddress` isn't the one just built.

   Skippable with `-SkipWaitForReady` / `skip_wait`, but skipping means the run
   proves only that a VM started. It does **not** prove the DHCP reservation
   is right, which is the single most common Phase 1 mistake.

### Why verification is more than "wait for port 22"

`user-data.tmpl.yaml` sets `power_state.mode: reboot`, because
`package_upgrade` can pull a new kernel and it's better to reboot into it now
than to discover it during an unrelated reboot later. So a fresh node comes
up, provisions, and *drops off the network again*. Anything that waits only
for port 22 reports success during the pre-reboot window and hands you a node
that's about to disappear.

[`lib\AerieSsh.ps1`](lib/AerieSsh.ps1) instead tracks
`/proc/sys/kernel/random/boot_id`, which changes on every boot: it records the
value seen on first contact, waits for it to change, and only then runs
`cloud-init status --wait`.

### Golden image

`Get-GoldenImage.ps1` downloads the official Debian or Ubuntu generic cloud
image, converts it to VHDX, and grows it to 32GB so cloud-init's `growpart`
module has room to expand into on first boot. It runs automatically when the
template is missing, or on its own:

```powershell
.\Get-GoldenImage.ps1 -Distro Debian -OutputPath D:\aerie\vm-templates\debian-13-genericcloud.vhdx
```

The output has no per-host state baked in, so building it once and
`robocopy`-ing it to the other hosts' `D:\aerie\vm-templates\` is faster than
rebuilding per host. A `.provenance.json` is written alongside it recording
which image, which hash, and which converter produced it.

Integrity checking is asymmetric because the two upstreams differ:

- The **distro image** is verified against the vendor's own `SHA512SUMS` /
  `SHA256SUMS`, fetched from the same directory. Automatic, and a mismatch
  fails the run.
- **qemu-img** (needed because Hyper-V's `Convert-VHD` only handles VHD/VHDX,
  not qcow2) comes from Cloudbase, which publishes no checksum file. So its
  URL and SHA256 are committed together in
  [`scripts/versions.json`](../versions.json), and every download is verified
  against that hash. Pass `-QemuImgZipPath` to use a zip you downloaded
  yourself, or `-QemuImgUrl` to fetch a different build — in both cases the
  committed hash no longer applies (it describes the committed URL), so the
  run warns unless you also pass `-QemuImgSha256`.

The pinned Cloudbase URL is versioned. If it 404s, check
[the qemu-img-windows page](https://cloudbase.it/qemu-img-windows/), then bump
the `url` and `sha256` in `versions.json` **together** — a bumped URL with a
stale hash fails the build, and a bumped URL with no hash is worse than no pin
at all. Get the new hash with `Get-FileHash -Algorithm SHA256 <the zip>`.

## Phase-specific usage

**Phase 0 — DR-restore scratch VM.** Historical: the Phase 0 restore gate
built a throwaway Docker host with `-ExtraPackages docker.io -DataDiskSizeGB 0`
and restored the Compose stack onto it. That stack no longer exists, and
[`docs/disaster-recovery.md`](../../docs/disaster-recovery.md)'s recovery path
is now the Provision sequence plus a CNPG restore — nothing this script needs a
special invocation for.

**Phase 1 — node VMs.** Use the real per-host memory split from
[the cluster plan](../../docs/plans/swarm/design.md) (16 / 24 / 24 GB) via `-MemoryGB`, and set `-DataDiskSizeGB`
to whatever you're carving out of the TB storage for Longhorn on that host.
Nothing k3s-specific goes in `-RunCmd` — that's a separate step, once this
node answers SSH: see [`../k3s/README.md`](../k3s/README.md) or dispatch
**Provision 1: Install k3s**.

## The part-time host

One host in an installation may belong to a person rather than to the cluster.
`Register-PersonalModeControl.ps1` installs the control that lets them take it
back for an evening and give it back afterwards, and
[`docs/plans/part-time-node.md`](../../docs/plans/part-time-node.md) carries the
reasoning. Three things about that host differ from the other three, and all
three are arguments rather than special cases in the code:

- Its VM is built with `-AutomaticStartAction Nothing`, so Hyper-V does not
  return the node after a host reboot. A boot-time task reads the persisted
  intention and decides instead — which is what makes a Windows Update reboot
  mid-evening harmless. It is still in the reboot stagger: it takes updates
  like any other machine.
- It gets **no Longhorn data disk** (`-DataDiskSizeGB 0`), and joins Longhorn
  with `allowScheduling: false`. Not on principle — the arithmetic is in that
  plan's finding 3.
- Its node is a k3s **agent**, so it has no apiserver of its own and every
  cluster operation the control performs is asked of a permanent server over
  SSH.

Nothing else about it is different, and that is deliberate: it is a normal
node, and the work that makes a node leaving a non-event is work the cluster
wanted anyway.

## Windows Update reboot staggering

Once at least one node VM exists, run **Actions → Stagger Windows Update
reboots → Run workflow** (or dispatch
[`stagger-update-reboots.yml`](../../.github/workflows/stagger-update-reboots.yml)
directly). It's the same dispatch, rerun-on-demand model as *Provision 0: New
node VM*, but where that workflow builds one host per run, this one always
reconciles all of them at once:

1. **Plan.** The `hosts` input (default `hyperv-host-0,hyperv-host-1,
   hyperv-host-2`) is a comma-separated list of runner labels, typed in by
   hand rather than discovered — the GitHub API for listing self-hosted
   runners is excluded from the automatic `GITHUB_TOKEN` entirely and would
   need a fine-grained PAT with `Administration: Read` on the repo. For a job
   that runs a couple of times a year, editing this input on a topology
   change is a smaller cost than minting and rotating that credential. The
   list is sorted and spread evenly across the week (3 hosts today →
   Monday/Wednesday/Friday), all at the same `reboot_hour`. The mapping only
   depends on sorted order, so rerunning with the same `hosts` value
   reassigns everyone consistently with no other bookkeeping. Check the job
   summary, or dispatch with `dry_run: true` to see the computed schedule
   without touching anything.
2. **Apply.** On each host's own runner,
   [`Set-UpdateRebootSchedule.ps1`](Set-UpdateRebootSchedule.ps1) upserts the
   "Configure Automatic Updates" policy under
   `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` — Windows
   itself then owns installing updates and rebooting on that host's assigned
   day/hour, no WSUS and no custom Scheduled Task required. One host failing
   doesn't block the others (`fail-fast: false`).

Rerun the workflow with an updated `hosts` value any time a host is added,
removed, or renamed.

## Still manual

- The one-time host prerequisites above (switch, runner, keypair).
- **The pfSense DHCP reservation.** Verification proves it's right; it doesn't
  create it.
- Physical-disk passthrough, if you'd rather Longhorn used a whole disk than a
  VHDX. These scripts always create a VHDX on `-DataDiskPath`'s volume.

## Scripts

| | |
|---|---|
| [`Initialize-AerieNode.ps1`](Initialize-AerieNode.ps1) | End-to-end entry point. Start here. |
| [`Get-GoldenImage.ps1`](Get-GoldenImage.ps1) | Builds the distro template VHDX. |
| [`New-AerieVM.ps1`](New-AerieVM.ps1) | Creates one VM from the template. Bypasses preflight — use directly when you know better than a check. |
| [`Test-NodeVm.ps1`](Test-NodeVm.ps1) | Reads one VM back and asserts it against the on-disk layout rule above — fixed OS disk, allocated, no checkpoints, static memory, the start action asked for — plus the guest's root filesystem over SSH. Read-only. Dispatch as **Verify: Node VM shape**. |
| [`Move-NodeOsDisk.ps1`](Move-NodeOsDisk.ps1) | Moves a live node's OS disk to the host's fastest volume as a fixed, larger VHDX, one node at a time. Also `-Rollback` and `-RemoveSourceDisk`. For a node built before the layout rule above; new nodes never need it. |
| [`Set-PersonalMode.ps1`](Set-PersonalMode.ps1) | The part-time host's control: `-Enter` hands the machine back (label, cordon, drain, quiesce the runner, stop the VM), `-Exit` takes it back, `-Status` reads everything and changes nothing, `-Reconcile` is what the boot task runs, `-AutoExit` the daily one. |
| [`Register-PersonalModeControl.ps1`](Register-PersonalModeControl.ps1) | Installs that control once, as an administrator: four Scheduled Tasks, the desktop user's run rights on two of them, two desktop shortcuts, the config and the key. Dispatch as **Provision 9: Personal mode control**. |
| [`lib/Watch-PersonalModeTask.ps1`](lib/Watch-PersonalModeTask.ps1) | What the shortcuts run: starts a task and tails its log from the user's own session, because a SYSTEM task's output is in session 0 where nobody can see it. |
| [`lib/New-NoCloudIso.ps1`](lib/New-NoCloudIso.ps1) | Builds the cloud-init seed ISO via Windows' built-in IMAPI2FS — no ADK or oscdimg needed. |
| [`lib/AerieSsh.ps1`](lib/AerieSsh.ps1) | Post-boot verification over SSH. |
| [`lib/Register-VmConsoleLogShipper.ps1`](lib/Register-VmConsoleLogShipper.ps1) | Registers the Scheduled Task that ships one VM's serial console to OpenSearch. |
| [`lib/Send-VmConsoleLog.ps1`](lib/Send-VmConsoleLog.ps1) | Drains a VM's COM1 named pipe and POSTs lines to Aerie.Api. What that Scheduled Task actually runs. |
| [`cloud-init/`](cloud-init/) | `user-data` / `meta-data` templates. |

## Troubleshooting

**Preflight says the switch is Internal/Private.** The VM would boot and then
be unreachable from the LAN with no DHCP — a failure that looks like a DHCP
problem for an hour. Recreate the switch bound to a physical NIC, per the
prerequisites.

**Timed out waiting for port 22, or SSH rejects the key.** Watch the console:
`vmconnect localhost <VMName>`, and log in there as `aerie` with the
break-glass console password (see step 3) to read
`/var/log/cloud-init-output.log` directly. A port-22 timeout is usually the
DHCP reservation not matching the MAC.

**`Permission denied (publickey)`.** Resist the urge to go rotate keys — the
message does not mean what it appears to. sshd returns it identically for a
rejected key and for an account that doesn't exist, and the second is far more
likely here. Work down this list, cheapest first; each step needs nothing from
the previous one:

1. **Log in at the console as `aerie`.** `Login incorrect` means the account
   was never created, so this was never a key problem. Go to step 3.
2. **Compare fingerprints.** Preflight prints the keypair's `SHA256:…`, and
   the acceptance report prints what actually landed in the VM's
   `authorized_keys`. Different keys, or no `authorized_keys` at all, tells
   you which half is wrong.
3. **Check the datasource.** `cloud-init status --long` reporting
   `DataSourceNone` means the seed ISO wasn't readable, so *none* of
   `user-data` was applied: no account, no keys, no packages. Mount
   `D:\aerie\VMs\<VMName>\seed.iso` and confirm its root holds files named
   exactly `user-data` and `meta-data` — names like `USERDA~1` mean the image
   lost Joliet and cloud-init skipped it. `Assert-NoCloudIso` is supposed to
   catch this at ISO-creation time.
4. **Confirm you're talking to the right machine.**
   `Get-NetNeighbor -IPAddress <ExpectedIP>` returns the MAC that currently
   holds the address; it should be the VM's.

**The VM has the right hostname, so cloud-init must have run.** No — this is
the single most misleading signal on the box. pfSense hands the reservation's
hostname over DHCP (option 12) and the guest's network stack applies it as the
transient hostname, with cloud-init uninvolved. A VM that applied *none* of its
`user-data` still shows `Debian GNU/Linux 13 aerie-node-1 tty1` at the console.
Check the datasource, not the hostname.

**`Get-VMNetworkAdapter` shows `IPAddresses : {}`.** Not conclusive on its
own. That list is populated by the guest KVP daemon from `hyperv-daemons`,
which cloud-init installs *over the network* — so an empty list is equally
consistent with "no lease yet" and "cloud-init hasn't finished `apt` yet". The
DHCP lease table on pfSense is the authoritative check for whether the VM's
MAC ever got an address.

**`systemd-ssh-generator: Failed to query local AF_VSOCK CID` on the console.**
Harmless, and unrelated to any SSH problem you're chasing. systemd tries to
set up its optional ssh-over-`AF_VSOCK` socket units at boot; Hyper-V exposes
`AF_HYPERV` rather than `AF_VSOCK`, the CID query returns `EADDRNOTAVAIL`, and
the generator skips those units. Normal `sshd` on TCP/22 is unaffected. Every
Debian 13 guest on Hyper-V logs this.

**"Something answered but didn't identify as ..."** Another host holds that
address, or the reservation points somewhere else.

**"UNPROTECTED PRIVATE KEY FILE".** Windows OpenSSH checks ACLs, not just
POSIX modes. Keys written by the scripts are locked down automatically; a key
you pass with `-SshPrivateKeyPath` needs its own ACL to grant only you.

**The run seems stuck creating the data disk.** A fixed-size VHDX is zeroed up
front, so a 200GB disk takes a long time on spinning storage. This is why the
workflow's `timeout-minutes` is 360.
