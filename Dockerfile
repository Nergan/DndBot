# Stage 1: Build environment
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies (optimizes Docker layer caching)
# We assume the .csproj is named BotDND.csproj based on your obj/ directory structure
COPY ["BotDND.csproj", "./"]
RUN dotnet restore "./BotDND.csproj"

# Copy the remaining source code and publish the application
COPY . .
RUN dotnet publish "BotDND.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime environment
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Copy the compiled binaries from the build stage
COPY --from=build /app/publish .

# Inform Docker that we intend to persist data here.
# NOTE: Because the app creates "dndbot.db" in Directory.GetCurrentDirectory() (/app),
# you MUST run the container with a bind mount to preserve your SQLite database.
# Example: docker run -d --name dndbot -v /path/on/host/dndbot.db:/app/dndbot.db dndbot-image
VOLUME ["/app"]

# Define the entry point for the container
ENTRYPOINT ["dotnet", "BotDND.dll"]