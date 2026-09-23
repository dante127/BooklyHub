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
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableDiagnostics=0

# Create non-root user for security
RUN adduser --disabled-password --home /app --gecos '' appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "BooklyHub.Api.dll"]
