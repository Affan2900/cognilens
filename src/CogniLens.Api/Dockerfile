FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY CogniLens.slnx .
COPY src/CogniLens.Api/CogniLens.Api.csproj src/CogniLens.Api/
COPY src/CogniLens.Core/CogniLens.Core.csproj src/CogniLens.Core/
COPY src/CogniLens.Infrastructure/CogniLens.Infrastructure.csproj src/CogniLens.Infrastructure/
RUN dotnet restore src/CogniLens.Api/CogniLens.Api.csproj

COPY src/CogniLens.Api/ src/CogniLens.Api/
COPY src/CogniLens.Core/ src/CogniLens.Core/
COPY src/CogniLens.Infrastructure/ src/CogniLens.Infrastructure/
RUN dotnet publish src/CogniLens.Api/CogniLens.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CogniLens.Api.dll"]
