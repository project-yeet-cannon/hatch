-- Derives the `service` field every log record is tagged with before it
-- reaches OpenSearch.
--
-- Default: the originating container's Compose service name. Every service
-- in compose.prod.yml / compose.observability.yml sets
-- `logging.options.labels: com.docker.compose.service`, which makes the
-- json-file driver attach that (Compose-assigned) label to each line under
-- record.attrs; that's what fluent-bit's `docker` parser decodes it into.
--
-- Override: Aerie.Api ships one container but fronts several web apps
-- (dashboard, admin, ...) via UiLogsController, which logs a structured
-- {Service} property (see Program.cs's JSON console formatter +
-- UiLogsController.cs). When present, State.Service wins over the
-- container-level default so those lines are attributed to the specific
-- frontend app instead of just "aerie-api".
function set_service(tag, timestamp, record)
    local changed = false

    local attrs = record["attrs"]
    if attrs and attrs["com.docker.compose.service"] then
        record["service"] = attrs["com.docker.compose.service"]
        record["attrs"] = nil
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
