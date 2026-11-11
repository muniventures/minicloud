FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime-agent
WORKDIR /src
ARG MINICLOUD_AGENT_RID=linux-x64
COPY modules/contracts modules/contracts
COPY modules/common modules/common
COPY modules/runtime-agent modules/runtime-agent
RUN case "$MINICLOUD_AGENT_RID" in \
      linux-x64) other_rid=linux-arm64 ;; \
      linux-arm64) other_rid=linux-x64 ;; \
      *) echo "Unsupported runtime agent RID: $MINICLOUD_AGENT_RID" >&2; exit 1 ;; \
    esac \
    && dotnet publish modules/runtime-agent/Minicloud.RuntimeAgent.csproj -c Release -r "$MINICLOUD_AGENT_RID" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "/agent/$MINICLOUD_AGENT_RID" \
    && dotnet publish modules/runtime-agent/Minicloud.RuntimeAgent.csproj -c Release -r "$other_rid" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "/agent/$other_rid"

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["modules/contracts/Minicloud.Contracts.csproj", "modules/contracts/"]
COPY ["modules/common/Minicloud.Common.csproj", "modules/common/"]
COPY ["modules/worker/Minicloud.Worker.csproj", "modules/worker/"]
RUN dotnet restore "modules/worker/Minicloud.Worker.csproj"
COPY . .
WORKDIR "/src/modules/worker"
RUN dotnet publish "Minicloud.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY LICENSE /usr/share/minicloud/LICENSE
COPY LICENSES /usr/share/minicloud/LICENSES
COPY --from=runtime-agent /agent/linux-x64/Minicloud.RuntimeAgent /app/runtime-agent/linux-x64/minicloud-agent
COPY --from=runtime-agent /agent/linux-arm64/Minicloud.RuntimeAgent /app/runtime-agent/linux-arm64/minicloud-agent
COPY templates/runtime/minicloud-agent.service templates/runtime/apply-minicloud-routing.py templates/runtime/install-minicloud-agent.sh /app/runtime-agent/
ENTRYPOINT ["dotnet", "Minicloud.Worker.dll"]
