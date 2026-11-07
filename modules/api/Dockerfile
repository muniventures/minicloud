FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY modules/contracts/Minicloud.Contracts.csproj modules/contracts/
COPY modules/common/Minicloud.Common.csproj modules/common/
COPY modules/api/Minicloud.Api.csproj modules/api/
RUN dotnet restore modules/api/Minicloud.Api.csproj

COPY modules/contracts modules/contracts
COPY modules/common modules/common
COPY modules/api modules/api
RUN dotnet publish modules/api/Minicloud.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Minicloud.Api.dll"]
