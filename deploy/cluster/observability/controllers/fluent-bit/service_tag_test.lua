-- Tests for ./service_tag.lua. Run them:
--
--   docker run --rm -v "$PWD":/work -w /work nickblah/lua:5.4-alpine \
--     lua service_tag_test.lua
--
-- Not wired into CI, which has no Lua runtime - this is a file to run when
-- editing the script beside it. It exists because the interesting cases here
-- are all ones that look right and are wrong: a digest read as a git sha, a
-- registry port read as a tag, and above all a browser's log line inheriting
-- the revision of the API that merely relayed it.

dofile("./service_tag.lua")

local failures = 0
local function check(name, got, want)
    if got ~= want then
        failures = failures + 1
        print(string.format("FAIL %s: got %s, want %s", name, tostring(got), tostring(want)))
    else
        print(string.format("ok   %s", name))
    end
end

local SHA = "b465267d5809bc0c672d250a1e7cc81b8714ed92"
local OTHER = "2d868dcb2fb27834c99fbb1c3805c6e7f882f170"

local function run(record)
    local _, _, out = set_aerie_revision("kube.x", 0, record)
    return out
end

-- An aerie-api line: revision from the deployed tag, no application code.
local api = run({ kubernetes = { container_image = "ghcr.io/o/aerie-api:20260828025358-" .. SHA } })
check("api tag", api["aerie_revision"], SHA)

-- A third-party workload: no field, ever.
local traefik = run({ kubernetes = { container_image = "docker.io/library/traefik:v3.1.2" } })
check("third-party absent", traefik["aerie_revision"], nil)

-- Deployed at a moving tag - the four images that ship :latest.
local latest = run({ kubernetes = { container_image = "ghcr.io/o/aerie-backup:latest" } })
check("latest absent", latest["aerie_revision"], nil)

-- A digest reference must never be read as a git sha.
local digest = run({ kubernetes = { container_image = "ghcr.io/o/aerie-api@sha256:8fbda8a4e3536042f7e960096909a46ae3247fb38837f826d4e5d7f7a4579873" } })
check("digest absent", digest["aerie_revision"], nil)

-- A registry port must not be read as a tag.
local port = run({ kubernetes = { container_image = "registry:5000/aerie-api" } })
check("registry port absent", port["aerie_revision"], nil)

-- A bare full-sha tag resolves too.
local bare = run({ kubernetes = { container_image = "ghcr.io/o/aerie-api:" .. SHA } })
check("bare sha tag", bare["aerie_revision"], SHA)

-- THE ONE THAT MATTERS: a browser line relayed by aerie-api carries the
-- browser's revision, and the relay's goes in its own field.
local relayed = run({
    kubernetes = { container_image = "ghcr.io/o/aerie-api:20260828025358-" .. SHA },
    State = { Service = "dashboard", AerieRevision = OTHER, AerieSequence = 4126, AerieRelayRevision = SHA },
})
check("relayed emitter wins", relayed["aerie_revision"], OTHER)
check("relayed relay field", relayed["aerie_relay_revision"], SHA)
check("relayed sequence", relayed["aerie_sequence"], 4126)

-- A relayed line from a bundle predating the field must NOT inherit the
-- relay's revision. This is the mis-attribution the whole design forbids.
local oldBundle = run({
    kubernetes = { container_image = "ghcr.io/o/aerie-api:20260828025358-" .. SHA },
    State = { Service = "dashboard", AerieRevision = nil, AerieRelayRevision = SHA },
})
check("old bundle stays absent", oldBundle["aerie_revision"], nil)
check("old bundle still names relay", oldBundle["aerie_relay_revision"], SHA)

-- A Hyper-V console line (VmConsoleLogsController) is relayed too.
local vm = run({
    kubernetes = { container_image = "ghcr.io/o/aerie-api:20260828025358-" .. SHA },
    State = { Service = "vm-console.node-3" },
})
check("vm console absent", vm["aerie_revision"], nil)

-- A null property from the JSON parser must not become a field.
local nulls = run({
    kubernetes = { container_image = "ghcr.io/o/aerie-api:latest" },
    State = { Service = "dashboard", AerieRevision = "", AerieSequence = 0 },
})
check("empty string ignored", nulls["aerie_revision"], nil)
check("zero sequence ignored", nulls["aerie_sequence"], nil)

-- set_service must still behave exactly as before.
local svc = run({ kubernetes = { container_name = "api" } })
local _, _, s = set_service("kube.x", 0, { kubernetes = { container_name = "api" }, State = { Service = "dashboard" } })
check("set_service override", s["service"], "dashboard")

if failures > 0 then print(failures .. " FAILURES"); os.exit(1) end
print("all passed")
