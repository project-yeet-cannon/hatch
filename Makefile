test:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

run:
	dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj

build-container:
	docker build -t aerie-api ./src/Aerie.Api
