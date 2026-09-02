up:
	docker compose up -d --build

db:
	docker compose up -d db

down:
	docker compose down

destroy:
	docker compose down -v

# The `enterprise-*` targets that stood the observability stack up locally are
# gone with compose.observability.yml (the cluster plan Phase 7b.9). Logs,
# metrics and status are cluster services now - see deploy/cluster/observability/.

build:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; dotnet build ./src/Aerie.Api/Aerie.Api.csproj'

run:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; \
	trap "kill 0" EXIT; \
	(cd ./src/Aerie.Web && nvm use && npm run dev -w apps/dashboard) & \
	dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj'

test: test-api test-web

test-api:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

# One `npm ci` at the workspace root, then each app in turn. The apps are still
# named one at a time rather than run with `--workspaces` so that the "==>" line
# says which one is building when something fails.
test-web:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; \
	set -e; \
	cd ./src/Aerie.Web && nvm use && npm ci; \
	echo "==> @aerie/lib"; \
	npm run test --if-present -w packages/lib; \
	for app in admin auth chrome dashboard design home modeler docs family; do \
		echo "==> $$app"; \
		npm run lint -w apps/$$app && npm run test --if-present -w apps/$$app && npm run build -w apps/$$app; \
	done'

# Which DbContext the ef-* targets act on. Defaults to the core schema; a module
# owns its own context and migrations folder (see src/Aerie.Api/Modules/README.md),
# so target one with e.g. `make ef-database-update context=StorageContext`.
context ?= AerieContext

# make ef-migration migration=MyMigrationName
ef-migration:
	dotnet ef migrations add $(migration) --context $(context) --project ./src/Aerie.Api/Aerie.Api.csproj

# Applies pending EF migrations to the running `db` container without starting the full app (no HA/Quartz dependency).
ef-database-update:
	dotnet ef database update --context $(context) --project ./src/Aerie.Api/Aerie.Api.csproj

# Opens a psql shell against the running `db` container's aerie database, for manual inspection.
db-shell:
	docker compose exec db psql -U user -d aerie

# The trading silo (docs/plans/trading.md Phase 0b). Its own target rather than
# a limb of `test`, for the same reason it gets its own CI lane: the silo is
# meant to be liftable into its own repository, and a Python failure surfacing
# as "make test is red" is the coupling starting. `make test` deliberately does
# not depend on this.
#
# uv manages the interpreter as well as the packages, so this needs no Python
# on the box - only uv itself. `--frozen` fails rather than re-resolving if
# uv.lock has drifted from pyproject.toml, which is the whole point of
# committing the lockfile: the same versions here and in CI.
#
# PYRIGHT_PYTHON_GLOBAL_NODE=off because pyright is a Node program wearing a
# Python wrapper, and by default that wrapper runs whatever `node` is first on
# PATH - at any version, including ones that cannot parse it. On this machine
# that is a v12 at /usr/local/bin/node, shadowed in an interactive shell by
# nvm and not shadowed here, and it fails as a JavaScript SyntaxError inside a
# minified bundle rather than as anything resembling "wrong node". Off makes
# nodeenv fetch pyright's own, so the type check is a function of uv.lock and
# not of what the box happens to have.
trading-test:
	cd ./src/Aerie.Trading && \
	export PYRIGHT_PYTHON_GLOBAL_NODE=off; \
	uv sync --frozen && \
	uv run ruff format --check . && \
	uv run ruff check . && \
	uv run pyright && \
	uv run pytest
