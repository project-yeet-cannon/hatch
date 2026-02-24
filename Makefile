up:
	docker compose up -d --build

down:
	docker compose down

destroy:
	docker compose down -v

build:
	dotnet build --project ./src/Aerie.Api/Aerie.Api.csproj

run:
	dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj

test:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

# make ef-migration migration=MyMigrationName
ef-migration:
	dotnet ef migrations add $(migration) --project ./src/Aerie.Api/Aerie.Api.csproj
