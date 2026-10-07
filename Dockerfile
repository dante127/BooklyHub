# Multi-stage Dockerfile for BooklyHub API (.NET 10)
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for optimal layer caching
COPY ["src/BooklyHub.Domain/BooklyHub.Domain.csproj", "src/BooklyHub.Domain/"]
COPY ["src/BooklyHub.Application/BooklyHub.Application.csproj", "src/BooklyHub.Application/"]
COPY ["src/BooklyHub.Infrastructure/BooklyHub.Infrastructure.csproj", "src/BooklyHub.Infrastructure/"]
COPY ["src/BooklyHub.Api/BooklyHub.Api.csproj", "src/BooklyHub.Api/"]
COPY ["BooklyHub.sln", "./"]

RUN dotnet restore "src/BooklyHub.Api/BooklyHub.Api.csproj"

# Copy full source and publish
COPY . .
WORKDIR "/src/src/BooklyHub.Api"
RUN dotnet publish "BooklyHub.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is not in this base image (measured: `command -v curl` finds nothing), and it is what the compose
# healthcheck needs to ask /health/live a real HTTP question. Nothing else in the image is an HTTP client.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
# Only 8080: the process binds http://+:8080 and holds no certificate, so an 8081 here would advertise a
# port nothing listens on (and UseHttpsRedirection was logging that it could not find an https port).
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableDiagnostics=0

# Create non-root user for security.
#
# This line had never run to completion: the runtime stage is Ubuntu 24.04.5, where `command -v adduser` finds
# nothing and `command -v useradd` does (measured on the pulled base image), so `adduser` ended the build with
# exit 127 and no image was ever produced. useradd is the spelling this base has, and it needs no
# prompt-suppressing flags because it never prompts. The fixed uid/gid is what a file owned by "the app user"
# means once the layer boundary is between the chown and the copy.
RUN useradd --uid 10001 --user-group --home-dir /app --shell /usr/sbin/nologin appuser \
    && chown 10001:10001 /app
USER appuser

# Root-owned and read-only for the process: nothing at runtime writes into /app, so the published output does
# not need the app user to own it.
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "BooklyHub.Api.dll"]
