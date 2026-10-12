# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AcxiomCRM.sln ./
COPY src/AcxiomCRM.Web/AcxiomCRM.Web.csproj src/AcxiomCRM.Web/
RUN dotnet restore src/AcxiomCRM.Web/AcxiomCRM.Web.csproj
COPY src/ src/
RUN dotnet publish src/AcxiomCRM.Web/AcxiomCRM.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
# Writable state (SQLite database + data-protection keys) lives on a volume owned by the non-root app user.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/acxiomcrm.db" \
    DataProtection__KeyPath=/data/keys \
    Security__UseForwardedHeaders=true
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "AcxiomCRM.Web.dll"]
