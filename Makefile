test:
	dotnet test ./src/Aerie.Api.Tests/Aerie.Api.Tests.csproj

run:
	dotnet run --project ./src/Aerie.Api/Aerie.Api.csproj

build-container:
	dotnet publish ./src/Aerie.Api/Aerie.Api.csproj --os linux --arch x64 /t:PublishContainer -c Release
