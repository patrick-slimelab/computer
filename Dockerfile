FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore
COPY ComputerBot/ComputerBot.csproj ComputerBot/
COPY matrix-dotnet-sdk/Matrix.Sdk/Matrix.Sdk.csproj matrix-dotnet-sdk/Matrix.Sdk/
RUN dotnet restore ComputerBot/ComputerBot.csproj

# Copy source
COPY ComputerBot/ ComputerBot/
COPY matrix-dotnet-sdk/ matrix-dotnet-sdk/
WORKDIR /src/ComputerBot
RUN dotnet publish -c Release -o /app/out

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .
CMD ["dotnet", "ComputerBot.dll"]
