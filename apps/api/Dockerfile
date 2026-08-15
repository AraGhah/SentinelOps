FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

COPY . .
RUN dotnet restore apps/api/SentinelOps.Api.csproj
RUN dotnet publish apps/api/SentinelOps.Api.csproj -c Release -o /app/publish --no-restore \
    /p:UseAppHost=false

# Runtime-only image (no SDK) — smaller attack surface for the Docker image
# scan and for anyone with shell access to the running container. Alpine base
# (musl, ~110MB runtime vs ~215MB on Debian) keeps the final image small.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

# wget ships with busybox on alpine already — used below by HEALTHCHECK, no
# extra package install needed.
RUN addgroup -S appgroup && adduser -S -D -H -u 5678 -G appgroup appuser

WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish .

USER appuser

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Container-level liveness check, independent of the ALB target group check
# (see ApiStack) — lets `docker ps` / ECS container-health surface a hung
# process even when nothing is routing traffic to it yet.
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:5000/healthz || exit 1

ENTRYPOINT ["dotnet", "SentinelOps.Api.dll"]
