-- The cluster plan Phase 6b.10 - a fork of ../../../../../containers/fluent-bit/
-- service_tag.lua, not an edit of it. That file is still bind-mounted into the
-- old host's compose fluent-bit (compose.observability.yml) and stays there
-- until Phase 7 deletes it - the same "do not edit compose.observability.yml
-- to match" rule the cluster plan's 6a.4 note gives for the Kuma admin
-- password applies here for the same reason: this container's log-tagging
-- would silently stop matching on the container-level branch below, on a host
-- still serving production, for a rewrite it never asked for.
--
-- Derives the `service` field every log record is tagged with before it
-- reaches OpenSearch.
--
-- Default: the originating container's name. Under compose this came from
-- record.attrs["com.docker.compose.service"] (the json-file driver's compose
-- label); under containerd there is no such label, and no compose-assigned
-- name to carry it. The ../fluent-bit.yaml `kubernetes` filter (unindexed by
-- Match, ahead of this one in the pipeline) attaches a `kubernetes` object to
-- every record instead, and record.kubernetes.container_name - the container
-- name from the pod spec, the closest equivalent this cluster has - is what
-- the default branch reads now.
--
-- Override: Aerie.Api ships one container but fronts several web apps
-- (dashboard, admin, ...) via UiLogsController, which logs a structured
-- {Service} property (see Program.cs's JSON console formatter +
-- UiLogsController.cs). When present, State.Service wins over the
-- container-level default so those lines are attributed to the specific
-- frontend app instead of just "aerie-api" - unchanged from the compose
-- version, and the reason this file exists at all: it is also what puts a
-- Hyper-V console line under `vm-console.<VMName>` (VmConsoleLogsController.cs).
-- Precedence is unchanged: the container-level default first, `State.Service`
-- overriding it.
function set_service(tag, timestamp, record)
    local changed = false

    local kubernetes = record["kubernetes"]
    if kubernetes and kubernetes["container_name"] then
        record["service"] = kubernetes["container_name"]
        changed = true
    end

    local state = record["State"]
    if state and state["Service"] then
        record["service"] = state["Service"]
        changed = true
    end

    if changed then
        return 1, timestamp, record
    end
    return 0, timestamp, record
end
