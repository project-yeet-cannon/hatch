[← Phase 8](phase-8-backup-v2.md) · [Design & decisions](design.md)

---

# Phase 9 — Productization + docs

When dissipating the swarm plans, turn this document into its own individual plan, just a single md doc in plans root. Add any other deferred productization/parameterization work that we didn't do previously.

**Status: DEFERRED to its own new plan**

- [ ] `scripts/bootstrap-node.sh` and `scripts/restore.sh`
- [ ] Helm `values.yaml` holding domain / HA / seed data, so someone else can run
      Phases 1-2 and get a working stack
- [ ] **Finish the site repo split that [Phase 5](phase-5-app-tier.md#additions-this-phase-makes-to-other-phases)
      starts.** Phase 5 creates a private per-installation repo holding one
      ConfigMap, purely so image automation has somewhere to commit that is not
      Aerie. The productization version generalizes it: the `HelmRelease` and
      its values move there, this repo ships `deploy/site-template/` as the
      seed, [`Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1) takes the
      site repo as an explicit parameter instead of deriving owner/repo from the
      dispatching run, and the installer creates the site repo from the
      template. That is what lets a second operator express per-installation
      *structure* — an extra Ingress, a different replica count, a workload
      Aerie does not ship — without forking. Decide, in the same work, whether
      `cluster-config.json`'s values collapse into the site repo rather than
      living in GitHub variables beside it, and whether committed encrypted
      values are now wanted there (a private repo is not a shared artifact, so
      the [SOPS objection](design.md#secrets--no-bytes-in-git) does not reach
      it — but two secret mechanisms is a cost of its own).
- [ ] `docs/cluster-architecture.md`, in the existing phase-doc style
- [ ] Cluster cold start, push-button solution. This is the end goal of two different lines of work - swarm and productization. Ultimately I want push-button cluster initialization for new users, as well as recovering users. It seems to me that these should be the same process; a new user is simply using a global template/baseline/seed data source rather than a personal backup. I presume this will be a lot of work and should be its own plan, but it is what we are building towards.

