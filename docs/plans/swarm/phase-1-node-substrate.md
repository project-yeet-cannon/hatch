[← Phase 0](phase-0-backup-and-dr.md) · [Design & decisions](design.md) · [Phase 2 →](phase-2-k3s-flux-secrets.md)

---

# Phase 1 — Node substrate

**Status: Complete** — the one unchecked item is a late addition, a manual
prerequisite [Phase 6](phase-6-observability.md) discovered it needs and that
belongs here rather than there. Nothing in Phases 2–5 waits on it.

> Provisioning is scripted in [`scripts/hyperv/`](../../../scripts/hyperv/): dispatch
> the **Provision 0: New node VM** workflow at that host's runner — the
> primary path, and the auditable one. Running `Initialize-AerieNode.ps1` by
> hand in an elevated session on the host is the same code path, kept only as
> a fallback for when the runner isn't up yet.
>
> **`[x]` here means the script does it, not that a machine exists yet** —
> unlike Phase 0, where every tick describes something already running. These
> stop being aspirational once the script has been run once per host; the
> unchecked items below are the ones that stay manual no matter how many times
> it runs.

## Phase 1 — The steps

- [x] One Hyper-V Linux VM per host (Debian 13 / Ubuntu 24.04 LTS), **external
      virtual switch** so each VM gets its own LAN IP; DHCP reservations on the MACs
      — *VM, distro and switch attachment are scripted; **the pfSense
      reservation and the one-time switch creation are not**. Preflight refuses
      to run against an Internal/Private switch, and verification fails the run
      if the reserved address doesn't answer as the node just built — so a
      wrong reservation surfaces immediately instead of during Phase 2*
- [x] **Enable MAC address spoofing on each vNIC** — without it the CNI silently
      drops pod traffic. Classic Hyper-V failure, painful to diagnose after the fact
      — *`New-AerieVM.ps1`: `Set-VMNetworkAdapter -MacAddressSpoofing On`*
- [x] Second fixed-size VHDX per VM (or physical disk passthrough) on the TB
      storage, for Longhorn
      — *`New-AerieVM.ps1`: `New-VHD -Fixed`, sized by `-DataDiskSizeGB`.
      Passthrough is still manual if you'd rather give Longhorn a whole disk*
- [x] VM auto-start on host boot; **Automatic Stop Action = Shut Down, not Save
      State** — saved-state VMs resume with clock skew that confuses etcd and certs
      — *`New-AerieVM.ps1`: `Set-VM -AutomaticStartAction Start
      -AutomaticStopAction ShutDown`*
- [x] NTP from pfSense on all three
      — *cloud-init installs chrony pointed at `-NtpServer`, and Hyper-V's own
      Time Synchronization integration service is disabled so it can't fight
      it. `chronyc sources` is in the post-boot report*
- [x] **Stagger Windows Update reboots across the three hosts** — quorum of 3
      tolerates one node down; two at once freezes the cluster
      — *scripted: `.github/workflows/stagger-update-reboots.yml` takes a
      typed-in list of `hyperv-host-*` runners, spreads them evenly across
      the week, and `scripts/hyperv/Set-UpdateRebootSchedule.ps1` upserts
      each host's Windows Update AU registry policy. Rerun with an updated
      `hosts` input whenever the host topology changes.*
- [x] apply staggering actions to all cluster servers
- [x] undo temporary dynamic disk sizing in New-AerieVM.ps1
- [x] **A pfSense DHCP reservation for each Windows host itself**, not only for
      the VM on it. Nothing built through Phase 5 cares what the hosts'
      addresses are — provisioning dispatches by runner *label*, the runners
      reach GitHub outbound, and VM traffic rides the external switch on the
      VM's own pinned MAC — so ordinary leases were correct up to here. [Phase
      6a.2](phase-6-observability.md) is what changes it: the three host
      addresses get pasted into `WINDOWS_EXPORTER_TARGETS` as a static
      Prometheus scrape list, and a lease that moves afterwards surfaces as a
      firing target-down alert rather than as the address change it is
      — *manual, like the VM reservations above, and with one difference worth
      knowing before you go looking for a MAC. `New-VMSwitch
      -AllowManagementOS $true` moves the host's own traffic onto a `vEthernet
      (ExternalSwitch)` adapter carrying its own MAC, so that is the one to
      reserve against — `Get-NetAdapter -Name 'vEthernet (ExternalSwitch)' |
      Select-Object Name, MacAddress` — and not the physical NIC's. Deleting
      and recreating the switch can change it, which the VM MACs, pinned
      explicitly by `New-AerieVM.ps1`, never do. Reserve on pfSense rather than
      setting a static address in Windows: one source of truth for LAN
      addressing, the same reasoning Phase 3a.2 applies to the VIP.*

