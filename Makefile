up:
	docker compose up -d --build

db:
	docker compose up -d db

down:
	docker compose down

destroy:
	docker compose down -v

build:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; dotnet build ./src/Aerie.Api/Aerie.Api.csproj'

run:
	bash -c 'export NVM_DIR="$$HOME/.nvm"; [ -s "$$NVM_DIR/nvm.sh" ] && \. "$$NVM_DIR/nvm.sh"; dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj'

test:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

# make ef-migration migration=MyMigrationName
ef-migration:
	dotnet ef migrations add $(migration) --project ./src/Aerie.Api/Aerie.Api.csproj

# Applies pending EF migrations to the running `db` container without starting the full app (no HA/Quartz dependency).
ef-database-update:
	dotnet ef database update --project ./src/Aerie.Api/Aerie.Api.csproj

# Opens a psql shell against the running `db` container's aerie database, for manual inspection.
db-shell:
	docker compose exec db psql -U user -d aerie
