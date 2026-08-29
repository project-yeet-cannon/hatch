-- The cluster plan Phase 6b.10 - a fork of the old compose stack's
-- containers/fluent-bit/service_tag.lua, not an edit of it. That file was
-- deliberately left untouched while the compose host was still serving
-- production, and 7b.9 deleted it along with the rest of that path; it is
-- reachable in git history if the divergence ever needs reading.
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

-- Derives `aerie_revision` - the git commit of the Aerie build that produced
-- the line - along with `aerie_sequence` and, on relayed lines only,
-- `aerie_relay_revision`. See docs/plans/version.md.
--
-- One field, one path, for every source. That is the whole requirement: an
-- operator reading the aggregator should never have to remember a different
-- route to the revision depending on which workload emitted the line.
--
-- Same precedence shape as set_service above, and for the same reason - a
-- container-level default that any application can override - but with one
-- extra rule that set_service does not need and that this field cannot work
-- without.
--
-- **A relayed line never falls back to the container image.** Aerie.Api
-- receives browser log lines through UiLogsController and writes them out as
-- its own stdout, so the `kubernetes.container_image` on a dashboard log
-- record is *aerie-api's*. Defaulting from it would stamp every browser's
-- line with the API's revision, and "which devices are running an old bundle"
-- - the question this field exists to answer - would return the same answer
-- for every row. So when State.Service says the line came from somewhere
-- else, the emitter's own claim is the only acceptable source, and its
-- absence leaves the field absent.
--
-- Third-party workloads get no field at all. Their image tags do not match the
-- shapes below, which is deliberate: `NOT _exists_:aerie_revision` reads as
-- "not built from this repo", where a field meaning "our commit" on one row
-- and "upstream chart version" on the next would read as nothing at all. What
-- version a third-party pod runs is already on every record anyway, as
-- kubernetes.container_image.

-- Guards every read below. Values arriving from the JSON parser can be null
-- (a .NET message property whose argument was null serialises that way), and
-- an empty or non-string value must not become a field.
local function nonempty(value)
    if type(value) == "string" and value ~= "" then
        return value
    end
    return nil
end

-- The git sha out of a container image reference, or nil for anything that
-- isn't one of Aerie's own tag shapes.
local function revision_from_image(image)
    if type(image) ~= "string" then
        return nil
    end

    -- A digest reference (`image@sha256:...`) carries a content hash, not a
    -- git sha. Rejected outright rather than parsed, so the two can never be
    -- confused for each other in a field named for one of them.
    if image:find("@", 1, true) then
        return nil
    end

    -- Everything after the last colon, refusing to cross a '/' - which is what
    -- keeps a registry port (`registry:5000/img`) from being read as a tag.
    local tag = image:match("^.*:([^:/]+)$")
    if not tag then
        return nil
    end

    -- <14-digit build timestamp>-<full sha>: what publish.yml emits for the
    -- images under Flux image automation, and the tag those actually deploy at.
    local stamped = tag:match("^%d%d%d%d%d%d%d%d%d%d%d%d%d%d%-(%x+)$")
    if stamped and #stamped == 40 then
        return stamped
    end

    -- A bare full sha. Every image this repo publishes carries this tag too,
    -- so anything pinned to it resolves here rather than going unlabelled.
    local bare = tag:match("^(%x+)$")
    if bare and #bare == 40 then
        return bare
    end

    return nil
end

function set_aerie_revision(tag, timestamp, record)
    local state = record["State"]

    -- The same property set_service keys on: its presence is what marks a line
    -- as having been produced by something other than this container.
    local relayed = state ~= nil and nonempty(state["Service"]) ~= nil

    local revision
    if relayed then
        revision = state and nonempty(state["AerieRevision"])
    else
        local kubernetes = record["kubernetes"]
        revision = kubernetes and revision_from_image(kubernetes["container_image"])

        -- A container that stamps itself wins over its own image tag. Nothing
        -- does this today; it is here so that an image deployed at a moving
        -- tag has a way to report a revision at all.
        if state then
            revision = nonempty(state["AerieRevision"]) or revision
        end
    end

    local changed = false

    if revision then
        record["aerie_revision"] = revision
        changed = true
    end

    if state then
        local sequence = tonumber(state["AerieSequence"])
        if sequence and sequence > 0 then
            record["aerie_sequence"] = sequence
            changed = true
        end

        local relay = nonempty(state["AerieRelayRevision"])
        if relay then
            record["aerie_relay_revision"] = relay
            changed = true
        end
    end

    if changed then
        return 1, timestamp, record
    end
    return 0, timestamp, record
end
