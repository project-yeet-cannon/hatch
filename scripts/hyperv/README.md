# Hyper-V VM provisioning

Repeatable creation of the Hyper-V Linux VMs `TODO_SWARM.md` needs in two
places: the Phase 0 scratch VM for the DR-restore gate, and the Phase 1 node
VMs (one per Windows host). Same VM shape both times — only the cloud-init
payload (`-ExtraPackages` / `-RunCmd`) differs.

## [x] Prerequisites

- Hyper-V role enabled, run from an elevated PowerShell session
- An **external** virtual switch already created. `-SwitchType External` isn't
  a real option here — Hyper-V infers "external" from binding a physical NIC,
  not from `-SwitchType` (whose `ValidateSet` is only `Internal`/`Private`,
  which is why passing `External` errors):

  ```powershell
  Get-NetAdapter -Physical | Where-Object Status -eq 'Up'   # find the NIC name
  New-VMSwitch -Name ExternalSwitch -NetAdapterName "<adapter name>" -AllowManagementOS $true
  ```

  `-AllowManagementOS $true` matters on these single-NIC home hosts — without
  it, creating the switch drops the host itself off the network.
- A qemu-img Windows build, to convert the vendor qcow2 image to VHDX — grab
  a zip from [Cloudbase's qemu-img-windows page](https://cloudbase.it/qemu-img-windows/)
  (not scripted here: release asset URLs are versioned and change)
- An SSH keypair to inject into the VM (`ssh-keygen -t ed25519`)
- pfSense DHCP reservations planned per VM — the workflow below expects you
  to pick each VM's MAC address up front

## [x] One-time: build the golden image

```powershell
.\Get-GoldenImage.ps1 -Distro Debian `
    -QemuImgZipPath D:\qemu-img-win-x64-2_3_0.zip `
    -OutputPath D:\vm-templates\debian-13-genericcloud.vhdx
```

This downloads Debian's (or Ubuntu's) official generic cloud image, converts
it to VHDX, and grows it to 32GB so cloud-init's `growpart` module has room
to expand into on first boot. Run it once, then copy the resulting VHDX to
every Hyper-V host that needs it (`robocopy` to each host's
`D:\vm-templates\`) rather than re-downloading/converting per host — the
file has no per-host state baked in.

Swap `-Distro Ubuntu` for Ubuntu 24.04 LTS instead of Debian 13; both are
valid per `TODO_SWARM.md`'s Phase 1 decision.

## Per VM: provision

```powershell
.\New-AerieVM.ps1 `
    -VMName aerie-dr-scratch `
    -GoldenImagePath D:\vm-templates\debian-13-genericcloud.vhdx `
    -SwitchName ExternalSwitch `
    -MacAddress 00-15-5D-01-02-03 `
    -SshPublicKeyPath ~\.ssh\id_ed25519.pub `
    -NtpServer 10.0.0.1 `
    -ExtraPackages docker.io `
    -DataDiskSizeGB 0
```

Register the DHCP reservation on pfSense for `-MacAddress` before or right
after running this — cloud-init needs DHCP to come up during first boot.

The script copies the golden VHDX (full copy, not a differencing disk — the
template stays safely reusable), builds a NoCloud seed ISO with this VM's
hostname/SSH key/NTP server baked in, creates a Generation 2 VM with Secure
Boot on the `MicrosoftUEFICertificateAuthority` template (required for a
shim-signed Linux guest), MAC spoofing on, static memory, and starts it.
Cloud-init installs packages and reboots itself once when done.

### Phase 0 — DR-restore scratch VM

Use `-ExtraPackages docker.io -DataDiskSizeGB 0` — the restore-gate procedure
in [`docs/disaster-recovery.md`](../../docs/disaster-recovery.md) only needs
Docker, not a Longhorn disk. `-RunCmd` can carry anything else that procedure
turns out to need (e.g. `git`, if you'd rather clone the repo than copy
compose files over manually).

### Phase 1 — node VMs

Use the real per-host memory split from `TODO_SWARM.md` (16 / 24 / 24 GB) via
`-MemoryGB`, and set `-DataDiskSizeGB` to whatever you're carving out of the
TB storage for Longhorn on that host. Nothing k3s-specific goes in
`-RunCmd` yet — that's Phase 2.

## What this does and doesn't automate

**Covered:** VM creation, disks, external switch attachment, MAC
assignment + spoofing, Secure Boot, static memory, disabling Hyper-V's own
time-sync integration service (so it can't fight the in-guest chrony/pfSense
NTP config cloud-init sets up), autostart/`ShutDown`-on-host-stop, first-boot
OS provisioning.

**Not covered, still manual:**

- Creating the external switch itself (one-time per host, do it in Hyper-V
  Manager or `New-VMSwitch`)
- The pfSense DHCP reservation
- Second physical/VHDX disk placement decisions (this script always creates
  a VHDX on `-VMStoragePath`'s volume, not a passthrough disk)
- Staggering Windows Update reboots across hosts — that's host-level policy,
  not a VM property
