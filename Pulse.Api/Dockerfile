FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy solution and project files first for layer caching
# COPY Pulse.sln .
COPY Pulse.Api/Pulse.Api.csproj                         Pulse.Api/
COPY Pulse.Application/Pulse.Application.csproj         Pulse.Application/
COPY Pulse.Domain/Pulse.Domain.csproj                   Pulse.Domain/
COPY Pulse.Infrastructure/Pulse.Infrastructure.csproj   Pulse.Infrastructure/

RUN dotnet restore Pulse.Api/Pulse.Api.csproj

COPY . .
# COPY src/backend/ src/backend/

RUN dotnet publish Pulse.Api/Pulse.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S pulse && adduser -S pulse -G pulse
USER pulse

COPY --from=build --chown=pulse:pulse /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -qO- http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Pulse.Api.dll"]
