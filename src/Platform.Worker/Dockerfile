# The base images are pinned by digest, not by tag. The tag moves; the digest
# does not, and what it freezes here is the ICU build that decides how this
# service formats text. The pinned runtime carries Ubuntu 24.04.4 LTS with
# libicu74 74.2-1ubuntu3.1, verified inside the image itself, and that is the
# exact ICU of the Ubuntu row of the divergence table in ADR-0017. Moving
# either line is a decision about output, not a maintenance chore: re-measure
# the table before changing it, and record the new digest with the ICU version
# it carries.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94

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
