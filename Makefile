up:
	docker compose up -d

down:
	docker compose down

destroy:
	docker compose down -v

test:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

run:
	dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj

build-container:
	docker build -t aerie-api ./src/Aerie.Api
