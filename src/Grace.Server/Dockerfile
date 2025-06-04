#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# Using the SDK image as the base instead of the aspnet image lets us run a shell for debugging.
FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview AS base
WORKDIR /app
EXPOSE 5000
EXPOSE 5001

# Install required packages for a better terminal experience
RUN apt-get update && apt-get install -y \
    bash \
    readline-common \
    locales \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Set locale to avoid character encoding issues
RUN echo "en_US.UTF-8 UTF-8" > /etc/locale.gen && \
    locale-gen en_US.UTF-8 && \
    update-locale LANG=en_US.UTF-8
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8

# Set bash as the default shell
SHELL ["/bin/bash", "-c"]

# Add default bashrc with history and keybinding configurations
RUN echo 'export PS1="\\u@\\h:\\w\\$ "' >> /etc/bash.bashrc && \
    echo "HISTFILE=/root/.bash_history" >> /etc/bash.bashrc && \
    echo "HISTSIZE=1000" >> /etc/bash.bashrc && \
    echo "HISTFILESIZE=2000" >> /etc/bash.bashrc

# Add .inputrc for keybinding corrections (up-arrow and others)
RUN echo '"\e[A": history-search-backward' >> /root/.inputrc && \
    echo '"\e[B": history-search-forward' >> /root/.inputrc && \
    echo '"\e[C": forward-char' >> /root/.inputrc && \
    echo '"\e[D": backward-char' >> /root/.inputrc

# Optional: Install nano or other text editors for better usability
RUN apt-get update && apt-get install -y nano && apt-get clean && rm -rf /var/lib/apt/lists/*

# Make sure bash is used as the default shell in interactive mode
CMD ["/bin/bash"]

# Set the PATH environment variable to include the dotnet tools directory
ENV PATH="$PATH:/root/.dotnet/tools"

# Set environment variables to configure ASP.NET Core
ENV ASPNETCORE_HTTPS_PORT=5001
ENV ASPNETCORE_HTTP_PORT=5000
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=true
ENV DOTNET_EnableDiagnostics=1

# Install the dotnet tools globally
RUN dotnet tool install dotnet-dump -g --verbosity q
RUN dotnet tool install dotnet-gcdump -g --verbosity q
RUN dotnet tool install dotnet-trace -g --verbosity q
RUN dotnet tool install dotnet-counters -g --verbosity q
RUN dotnet tool install dotnet-monitor -g --verbosity q

#================================================================================================

FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview AS build
WORKDIR /src

# Restoring individual projects to take advantage of layer caching.
COPY ["nuget.config", "nuget.config"]

COPY ["Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj", "Grace.Aspire.ServiceDefaults/"]
RUN dotnet restore "Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj"
COPY Grace.Aspire.ServiceDefaults/ Grace.Aspire.ServiceDefaults/
RUN dotnet build "Grace.Aspire.ServiceDefaults/Grace.Aspire.ServiceDefaults.csproj" --no-restore -c Debug /p:DebugType=full

COPY ["CosmosSerializer/CosmosJsonSerializer.csproj", "CosmosSerializer/"]
RUN dotnet restore "CosmosSerializer/CosmosJsonSerializer.csproj"
COPY CosmosSerializer/ CosmosSerializer/
RUN dotnet build "CosmosSerializer/CosmosJsonSerializer.csproj" --no-restore -c Debug /p:DebugType=full

COPY ["Grace.Types/Grace.Types.fsproj", "Grace.Types/"]
COPY ["Grace.Shared/Grace.Shared.fsproj", "Grace.Shared/"]
RUN dotnet restore "Grace.Types/Grace.Types.fsproj"
COPY Grace.Types/ Grace.Types/
COPY Grace.Shared/ Grace.Shared/
RUN dotnet build "Grace.Types/Grace.Types.fsproj" --no-restore -c Debug /p:DebugType=full

RUN dotnet restore "Grace.Shared/Grace.Shared.fsproj"
COPY Grace.Shared/ Grace.Shared/
RUN dotnet build "Grace.Shared/Grace.Shared.fsproj" --no-restore -c Debug /p:DebugType=full

COPY ["Grace.Actors/Grace.Actors.fsproj", "Grace.Actors/"]
RUN dotnet restore "Grace.Actors/Grace.Actors.fsproj"
COPY Grace.Actors/ Grace.Actors/
RUN dotnet build "Grace.Actors/Grace.Actors.fsproj" --no-restore -c Debug /p:DebugType=full

COPY ["Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj", "Grace.Orleans.CodeGen/"]
RUN dotnet restore "Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj"
COPY Grace.Orleans.CodeGen/ Grace.Orleans.CodeGen/
RUN dotnet build "Grace.Orleans.CodeGen/Grace.Orleans.CodeGen.csproj" --no-restore -c Debug /p:DebugType=full

COPY ["Grace.Server/Grace.Server.fsproj", "Grace.Server/"]
RUN dotnet restore "Grace.Server/Grace.Server.fsproj"
COPY Grace.Server/ Grace.Server/
RUN dotnet build "Grace.Server/Grace.Server.fsproj" --no-restore -c Debug /p:DebugType=full

COPY Grace.Server/web.config /app/publish/web.config

#================================================================================================

FROM build AS publish
RUN dotnet publish "Grace.Server/Grace.Server.fsproj" -c Debug -o /app/publish --verbosity m --no-build /p:UseAppHost=false /p:PublishProfile=Properties/PublishProfiles/DisableContainerBuild.pubxml
#RUN ls -alR /app/publish

#================================================================================================

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Copy .pdb files to a separate directory
RUN mkdir -p /app/symbols
RUN find /app -name "*.pdb" -exec cp {} /app/symbols/ \;

# RUN ls -alR /app

ENTRYPOINT ["dotnet", "Grace.Server.dll"]
