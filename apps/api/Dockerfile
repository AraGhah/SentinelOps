FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

COPY . .
RUN dotnet restore apps/api/SentinelOps.Api.csproj

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

CMD ["dotnet", "run", "--project", "apps/api/SentinelOps.Api.csproj", "--no-launch-profile"]
