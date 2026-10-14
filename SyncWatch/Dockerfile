FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
# Publish the web project directly to avoid NETSDK1194 and solution-level output issues
RUN dotnet publish ./SyncWatch/SyncWatch.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 10000
ENTRYPOINT ["dotnet", "SyncWatch.dll"]
