#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["Grace.Server/Grace.Server.fsproj", "Grace.Server/"]
COPY ["CosmosSerializer/CosmosJsonSerializer.csproj", "CosmosSerializer/"]
COPY ["Grace.Actors/Grace.Actors.fsproj", "Grace.Actors/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "Grace.Shared/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj"
COPY . .
WORKDIR "/src/Grace.Server"
RUN dotnet build "Grace.Server.fsproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Grace.Server.fsproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Grace.Server.dll"]