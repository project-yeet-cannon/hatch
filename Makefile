up:
	docker compose up -d --build

db:
	docker compose up -d db

down:
	docker compose down

destroy:
	docker compose down -v

build:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; dotnet build ./src/Hatch.Api/Hatch.Api.csproj'

run:
	dotnet run --project ./src/Hatch.Api/Hatch.Api.csproj

test: test-api test-hatch test-web

test-api:
	dotnet test ./src/Hatch.Api.Tests/Hatch.Api.Tests.csproj

# The CLI - `hatch`, which lives in src/Hatch.Cli. Its own target because it
# is what somebody editing the loop runs, and because it needs neither a
# database nor a node: a stub wire and a stub session are the whole fixture.
test-hatch:
	dotnet test ./src/Hatch.Cli.Tests/Hatch.Cli.Tests.csproj

# A built CLI for this machine, so that `hatch.sh work` starts in milliseconds
# rather than spending a few seconds in `dotnet run` deciding whether to build
# first. It is an optimisation and not a requirement - hatch.sh falls back to
# the SDK, and says so if there is neither.
#
# Deliberately still a plain, fast, host-RID Release build. `go-to-work` asks to
# be restarted as a newer build when its own source changes, and hatch.sh's
# supervisor calls this to make one - at two in the morning, between increments.
# The shippable artifact is `publish-hatch` below, which is a different and much
# slower thing aimed at a different consumer.
build-hatch:
	dotnet build ./src/Hatch.Cli/Hatch.Cli.csproj --configuration Release

# The artifact somebody else downloads: one file, no SDK on the far side, for
# every platform this house runs on. Self-contained because an operator taking
# Hatch for the first time has no .NET; single-file because the whole promise is
# "put it on your PATH"; trimmed because the alternative is 70MB of framework
# nobody calls.
#
# Each RID lands in artifacts/hatch/<rid>/, which is what AERIE-935's Runner
# page serves from.
HATCH_RIDS = win-x64 osx-arm64 osx-x64 linux-x64

publish-hatch:
	for rid in $(HATCH_RIDS); do \
		echo "==> $$rid"; \
		dotnet publish ./src/Hatch.Cli/Hatch.Cli.csproj \
			--runtime $$rid \
			--configuration Release \
			--output ./artifacts/hatch/$$rid \
			-p:PublishSingleFile=true \
			-p:SelfContained=true \
			-p:PublishTrimmed=true || exit 1; \
	done

# The same suite with the claim's tests turned on. They need a real Postgres and
# skip loudly without one (src/Hatch.Api.Tests/Hatch/HatchDatabase.cs): the claim
# is built out of conditional UPDATEs, and EF's in-memory provider cannot execute
# one at all - so `make test-api` alone can be green having verified none of
# them, and CI runs this lane rather than that one.
#
# The database named here is dropped and recreated. It is deliberately not the
# `hatch` database `make db` serves the app from, and nothing discovers a
# connection string on its own for the same reason: a test that TRUNCATEs what
# it finds should have been told exactly what to find.
test-api-db: db
	HATCH_TEST_DATABASE_URL="Host=localhost;Port=5432;Database=hatch_test;Username=user;Password=password" \
		dotnet test ./src/Hatch.Api.Tests/Hatch.Api.Tests.csproj

# One `npm ci` at the workspace root, then each app in turn. The apps are still
# named one at a time rather than run with `--workspaces` so that the "==>" line
# says which one is building when something fails.
test-web:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; \
	set -e; \
	cd ./src/Hatch.Web && nvm use && npm ci; \
	for app in design hatch; do \
		echo "==> $$app"; \
		npm run lint -w apps/$$app && npm run test --if-present -w apps/$$app && npm run build -w apps/$$app; \
	done'

# Which DbContext the ef-* targets act on. Defaults to the core schema; a module
# owns its own context and migrations folder (see src/Hatch.Api/Modules/README.md),
# so target one with e.g. `make ef-database-update context=StorageContext`.
context ?= AppDbContext

# make ef-migration migration=MyMigrationName
ef-migration:
	dotnet ef migrations add $(migration) --context $(context) --project ./src/Hatch.Api/Hatch.Api.csproj

# Applies pending EF migrations to the running `db` container without starting the full app (no HA/Quartz dependency).
ef-database-update:
	dotnet ef database update --context $(context) --project ./src/Hatch.Api/Hatch.Api.csproj

# Opens a psql shell against the running `db` container's hatch database, for manual inspection.
db-shell:
	docker compose exec db psql -U user -d hatch
