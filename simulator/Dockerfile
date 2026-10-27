FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY F1.Simulator.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "F1.Simulator.dll"]
