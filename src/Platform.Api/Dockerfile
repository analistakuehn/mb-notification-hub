ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${RUNTIME_IMAGE} AS base
WORKDIR /app
EXPOSE 8080

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["global.json", "."]
COPY ["src/Platform.Api/Platform.Api.csproj", "src/Platform.Api/"]
RUN dotnet restore "src/Platform.Api/Platform.Api.csproj"
COPY . .
WORKDIR "/src/src/Platform.Api"
RUN dotnet publish "Platform.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NotificationHub.Api.dll"]
