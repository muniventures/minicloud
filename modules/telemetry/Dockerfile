FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY modules/contracts/Minicloud.Contracts.csproj modules/contracts/
COPY modules/common/Minicloud.Common.csproj modules/common/
COPY modules/telemetry/Minicloud.Telemetry.csproj modules/telemetry/
RUN dotnet restore modules/telemetry/Minicloud.Telemetry.csproj

COPY modules/contracts modules/contracts
COPY modules/common modules/common
COPY modules/telemetry modules/telemetry
RUN dotnet publish modules/telemetry/Minicloud.Telemetry.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl wget \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
COPY LICENSE /usr/share/minicloud/LICENSE
COPY LICENSES /usr/share/minicloud/LICENSES
ENTRYPOINT ["dotnet", "Minicloud.Telemetry.dll"]
