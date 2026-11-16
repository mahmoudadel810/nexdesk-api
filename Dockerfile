FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NexDesk.Domain/NexDesk.Domain.csproj src/NexDesk.Domain/
COPY src/NexDesk.Application/NexDesk.Application.csproj src/NexDesk.Application/
COPY src/NexDesk.Infrastructure/NexDesk.Infrastructure.csproj src/NexDesk.Infrastructure/
COPY src/NexDesk.Api/NexDesk.Api.csproj src/NexDesk.Api/
RUN dotnet restore src/NexDesk.Api/NexDesk.Api.csproj

COPY src/ src/
RUN dotnet publish src/NexDesk.Api/NexDesk.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "NexDesk.Api.dll"]
