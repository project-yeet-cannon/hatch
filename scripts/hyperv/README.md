# Hyper-V node provisioning

Repeatable creation of the Hyper-V Linux VMs `TODO_SWARM.md` needs in two
places: the Phase 0 scratch VM for the DR-restore gate, and the Phase 1 node
VMs (one per Windows host). Same VM shape both times — only the cloud-init
payload (`-ExtraPackages` / `-RunCmd`) differs.

## Two ways to run this, and they are the same thing

[`Initialize-AerieNode.ps1`](Initialize-AerieNode.ps1) is the entry point, and
it holds all the logic. There are two supported ways to invoke it, and neither
is a degraded version of the other:

| | **A — on the host** | **B — GitHub Actions** |
|---|---|---|
| How | elevated PowerShell on the Hyper-V host | Actions → *Provision node VM* → Run workflow |
| Needs | the `scripts\hyperv\` folder | a labelled self-hosted runner on that host |
| Config | parameters you type | workflow inputs + repo variables |
| Output | console | console + job summary |
| Actions minutes | n/a | **zero** — `runs-on` pins `self-hosted` |

The workflow checks out this repo on the target host and calls the same script
with the same parameters. Nothing lives in the workflow that doesn't live in
the script, which is what keeps the two from drifting.

The scripts have **no dependencies outside this directory**. `robocopy` (or
just copy) `scripts\hyperv\` onto a host and flow A works standalone — no repo
clone, no runner, no network path back to GitHub.

Use whichever fits: flow A when you're already on the box or the runner isn't
up yet, flow B for an auditable record of who built which node when.

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

3. **The OpenSSH client**, for post-boot verification:

   ```powershell
   Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
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
   rename them in [`provision-node.yml`](../../.github/workflows/provision-node.yml)
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
   | `QEMU_IMG_SHA256` | variable | no | pins the qemu-img download — see [Golden image](#golden-image) |
   | `VM_LOG_SHIPPER_TOKEN` | secret | no | already set for `cd.yml`; without it, console-log shipping (below) is skipped |

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
    debian-13-genericcloud.vhdx
    debian-13-genericcloud.vhdx.provenance.json
  VMs\                                 -VMStoragePath — Hyper-V config plus one directory per VM
    aerie-node-1\
      os-disk.vhdx                     full copy of the template, not a differencing disk
      data-disk.vhdx                   fixed-size, unformatted; Longhorn claims it in Phase 3
      seed.iso                         NoCloud cloud-init seed for this VM
      console-log-shipper.ps1          only when console-log shipping is on - see below
```

Both roots are defaults, not assumptions — pass `-VMStoragePath` /
`-TemplatePath` (or the workflow's `vm_storage_path` / `template_path`) to put
either somewhere else, including on different volumes. Missing directories are
created on first run.

## Flow A — on the host

Elevated PowerShell, in `scripts\hyperv\`:

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

Check the host is ready without building anything by adding `-PreflightOnly` —
worth doing first on a host you haven't provisioned before, since it catches a
missing switch, a wrong switch type, a taken MAC, an occupied IP, or a too-full
volume in about a second.

## Flow B — GitHub Actions

Actions → **Provision node VM** → Run workflow. Pick the `host` matching the
runner label, fill in `vm_name`, `mac_address`, `expected_ip`, and the
per-host `memory_gb` / `data_disk_gb`. `preflight_only` is available as a
checkbox and does the same thing as above.

The job writes a summary with the node's address, resources, and the full
post-boot report.

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

   If `-LogIngestUrl`/`-LogIngestToken` are supplied (flow B sets these
   automatically whenever the `VM_LOG_SHIPPER_TOKEN` secret is present — see
   above), the VM's serial console (COM1, wired to a named pipe — Hyper-V has
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
  not qcow2) comes from Cloudbase, which publishes no checksum file. So it
  gets trust-on-first-use pinning: the first run prints the hash and warns
  that the download was unverified; set that value as the `QEMU_IMG_SHA256`
  repository variable (or pass `-QemuImgSha256`) and every later run verifies
  it. Pass `-QemuImgZipPath` instead to use a zip you downloaded yourself.

The pinned Cloudbase URL is versioned. If it 404s, check
[the qemu-img-windows page](https://cloudbase.it/qemu-img-windows/) and bump
`-QemuImgUrl`'s default.

## Phase-specific usage

**Phase 0 — DR-restore scratch VM.** `-ExtraPackages docker.io
-DataDiskSizeGB 0` — the restore-gate procedure in
[`docs/disaster-recovery.md`](../../docs/disaster-recovery.md) only needs
Docker, not a Longhorn disk. `-RunCmd` can carry anything else it turns out to
need (e.g. `git`, if you'd rather clone the repo than copy compose files over).

**Phase 1 — node VMs.** Use the real per-host memory split from
`TODO_SWARM.md` (16 / 24 / 24 GB) via `-MemoryGB`, and set `-DataDiskSizeGB`
to whatever you're carving out of the TB storage for Longhorn on that host.
Nothing k3s-specific goes in `-RunCmd` yet — that's Phase 2.

## Still manual

- The one-time host prerequisites above (switch, runner, keypair).
- **The pfSense DHCP reservation.** Verification proves it's right; it doesn't
  create it.
- Physical-disk passthrough, if you'd rather Longhorn used a whole disk than a
  VHDX. These scripts always create a VHDX on `-VMStoragePath`'s volume.
- **Staggering Windows Update reboots across hosts** — quorum of 3 tolerates
  one node down, two at once freezes the cluster. That's host-level policy,
  not a VM property.

## Scripts

| | |
|---|---|
| [`Initialize-AerieNode.ps1`](Initialize-AerieNode.ps1) | End-to-end entry point. Start here. |
| [`Get-GoldenImage.ps1`](Get-GoldenImage.ps1) | Builds the distro template VHDX. |
| [`New-AerieVM.ps1`](New-AerieVM.ps1) | Creates one VM from the template. Bypasses preflight — use directly when you know better than a check. |
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
`vmconnect localhost <VMName>`. A port-22 timeout is usually the DHCP
reservation not matching the MAC; a rejected key usually shows up as a
cloud-init failure to apply `ssh_authorized_keys` in the console output.

**"Something answered but didn't identify as ..."** Another host holds that
address, or the reservation points somewhere else.

**"UNPROTECTED PRIVATE KEY FILE".** Windows OpenSSH checks ACLs, not just
POSIX modes. Keys written by the scripts are locked down automatically; a key
you pass with `-SshPrivateKeyPath` needs its own ACL to grant only you.

**The run seems stuck creating the data disk.** A fixed-size VHDX is zeroed up
front, so a 200GB disk takes a long time on spinning storage. This is why the
workflow's `timeout-minutes` is 360.
