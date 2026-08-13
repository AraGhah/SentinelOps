FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY . .
RUN dotnet restore apps/api/SentinelOps.Api.csproj
RUN dotnet publish apps/api/SentinelOps.Api.csproj -c Release -o /app/publish --no-restore

# Runtime-only image (no SDK) — smaller attack surface for the Docker image
# scan and for anyone with shell access to the running container.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN adduser --disabled-password --gecos "" --uid 5678 appuser
USER appuser

WORKDIR /app
COPY --from=build --chown=appuser:appuser /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "SentinelOps.Api.dll"]
