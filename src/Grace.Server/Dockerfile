#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
#EXPOSE 5000
#EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Grace.Server/Grace.Server.fsproj", "Grace.Server/"]
COPY ["Grace.Actors/Grace.Actors.fsproj", "Grace.Actors/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "Grace.Shared/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj"
COPY . .
WORKDIR "/src/Grace.Server"
RUN dotnet build "Grace.Server.fsproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Grace.Server.fsproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "run", "Grace.Server.dll"]
