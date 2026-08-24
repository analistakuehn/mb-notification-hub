ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["global.json", "."]
COPY ["NuGet.Config", "."]
COPY ["src/Platform.Api/Platform.Api.csproj", "src/Platform.Api/"]
COPY ["src/Platform.Api/packages.lock.json", "src/Platform.Api/"]
COPY ["src/Platform.Worker/Platform.Worker.csproj", "src/Platform.Worker/"]
COPY ["src/Platform.Worker/packages.lock.json", "src/Platform.Worker/"]
RUN dotnet restore "src/Platform.Worker/Platform.Worker.csproj" --locked-mode
COPY . .
RUN rm -f src/Platform.Api/appsettings.json src/Platform.Api/appsettings.Development.json
RUN dotnet publish "src/Platform.Worker/Platform.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM ${RUNTIME_IMAGE} AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NotificationHub.Worker.dll"]
