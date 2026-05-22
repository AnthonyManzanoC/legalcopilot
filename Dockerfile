# 1. Usamos la versión basada en Ubuntu (Jammy) para compilar
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src
COPY LegalPilot.sln ./
COPY NuGet.Config ./
COPY src/LegalPilot.Api/LegalPilot.Api.csproj src/LegalPilot.Api/
COPY src/LegalPilot.Tests/LegalPilot.Tests.csproj src/LegalPilot.Tests/
RUN dotnet restore src/LegalPilot.Api/LegalPilot.Api.csproj
COPY . .
RUN dotnet publish src/LegalPilot.Api/LegalPilot.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# 2. Usamos Ubuntu (Jammy) para el entorno de ejecución (Evita el Segfault 139)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime
WORKDIR /app

# Puerto oficial actualizado para .NET 8
ENV ASPNETCORE_HTTP_PORTS=8080

# 3. Damos permisos explícitos a la carpeta App_Data para el usuario de .NET
USER root
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
USER app

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "LegalPilot.Api.dll"]