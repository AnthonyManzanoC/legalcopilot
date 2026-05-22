FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LegalPilot.sln ./
COPY NuGet.Config ./
COPY src/LegalPilot.Api/LegalPilot.Api.csproj src/LegalPilot.Api/
COPY src/LegalPilot.Tests/LegalPilot.Tests.csproj src/LegalPilot.Tests/
RUN dotnet restore src/LegalPilot.Api/LegalPilot.Api.csproj
COPY . .
RUN dotnet publish src/LegalPilot.Api/LegalPilot.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
VOLUME ["/app/App_Data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "LegalPilot.Api.dll"]
