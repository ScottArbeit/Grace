FROM mcr.microsoft.com/dotnet/sdk@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src

# Cache the Grace Server project graph restore separately from source changes.
COPY ["nuget.config", "nuget.config"]
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["Grace.Types/Grace.Types.fsproj", "Grace.Types/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "Grace.Shared/"]
COPY ["Grace.Actors/Grace.Actors.fsproj", "Grace.Actors/"]
COPY ["Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj", "Grace.Orleans.CodeGen/"]
COPY ["Grace.Server/Grace.Server.fsproj", "Grace.Server/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj" --runtime linux-x64

COPY Grace.Types/ Grace.Types/
COPY Grace.Shared/ Grace.Shared/
COPY Grace.Actors/ Grace.Actors/
COPY Grace.Orleans.CodeGen/ Grace.Orleans.CodeGen/
COPY Grace.Server/ Grace.Server/

FROM build AS publish
RUN dotnet publish "Grace.Server/Grace.Server.fsproj" --no-restore -c Release -o /app/publish --verbosity m /p:UseAppHost=false /p:DebugType=None /p:DebugSymbols=false /p:PublishProfile=Properties/PublishProfiles/DisableContainerBuild.pubxml

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_HTTP_PORTS=5000
ENV DOTNET_EnableDiagnostics=0
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Grace.Server.dll"]
