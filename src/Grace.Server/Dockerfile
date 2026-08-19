FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restoring individual projects to take advantage of layer caching.
COPY ["nuget.config", "nuget.config"]

COPY ["Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj", "Grace.Aspire.ServiceDefaults/"]
RUN dotnet restore "Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj"
COPY Grace.Aspire.ServiceDefaults/ Grace.Aspire.ServiceDefaults/
RUN dotnet build "Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj" --no-restore -c Release

COPY ["Grace.Types/Grace.Types.fsproj", "Grace.Types/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "Grace.Shared/"]
RUN dotnet restore "Grace.Types/Grace.Types.fsproj"
COPY Grace.Types/ Grace.Types/
COPY Grace.Shared/ Grace.Shared/
RUN dotnet build "Grace.Types/Grace.Types.fsproj" --no-restore -c Release

RUN dotnet restore "Grace.Shared/Grace.Shared.fsproj"
COPY Grace.Shared/ Grace.Shared/
RUN dotnet build "Grace.Shared/Grace.Shared.fsproj" --no-restore -c Release

COPY ["Grace.Actors/Grace.Actors.fsproj", "Grace.Actors/"]
RUN dotnet restore "Grace.Actors/Grace.Actors.fsproj"
COPY Grace.Actors/ Grace.Actors/
RUN dotnet build "Grace.Actors/Grace.Actors.fsproj" --no-restore -c Release

COPY ["Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj", "Grace.Orleans.CodeGen/"]
RUN dotnet restore "Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj"
COPY Grace.Orleans.CodeGen/ Grace.Orleans.CodeGen/
RUN dotnet build "Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj" --no-restore -c Release

COPY ["Grace.Server/Grace.Server.fsproj", "Grace.Server/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj"
COPY Grace.Server/ Grace.Server/
RUN dotnet build "Grace.Server/Grace.Server.fsproj" --no-restore -c Release

COPY Grace.Server/web.config /app/publish/web.config

FROM build AS publish
RUN dotnet publish "Grace.Server/Grace.Server.fsproj" -c Release -o /app/publish --verbosity m /p:UseAppHost=false /p:DebugType=None /p:DebugSymbols=false /p:PublishProfile=Properties/PublishProfiles/DisableContainerBuild.pubxml

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_HTTP_PORTS=5000
ENV DOTNET_EnableDiagnostics=0
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Grace.Server.dll"]
