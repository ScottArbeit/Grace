#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.


# Using the SDK image as the base instead of the aspnet image lets us run a shell for debugging.
#FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS base
WORKDIR /app
EXPOSE 5000
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Grace.Server/Grace.Server.fsproj", "./Grace.Server/"]
COPY ["Grace.Actors/Grace.Actors.fsproj", "./Grace.Actors/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "./Grace.Shared/"]
COPY ["CosmosSerializer/CosmosJsonSerializer.csproj", "./CosmosSerializer/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj"
COPY . .
WORKDIR "/src/Grace.Server"
#RUN dotnet build "Grace.Server.fsproj" -c Debug -o /app/build

FROM build AS publish
RUN dotnet publish "Grace.Server.fsproj" -c Debug -o /app/publish /p:UseAppHost=false

# Set environment variables to configure ASP.NET Core to use the certificate
ENV ASPNETCORE_URLS="https://+;http://+"
ENV ASPNETCORE_HTTPS_PORT=5001
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Grace.Server.dll"]
