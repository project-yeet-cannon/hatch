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
