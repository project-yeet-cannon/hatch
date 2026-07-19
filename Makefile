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
