# Eminova.Host — multi-stage build. Produces a ~110 MB image running as a non-root user.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
# Patch the POC dashboard into wwwroot/index.html (tenant code passed at build time)
ARG TENANT=default
RUN dotnet run --project tools/Eminova.Patch -c Release -- dashboard/EMINOVA_HR_Payroll_Workbench.html src/Eminova.Host/wwwroot/index.html ${TENANT}
RUN dotnet publish src/Eminova.Host/Eminova.Host.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
 && groupadd -r eminova && useradd -r -g eminova eminova && mkdir -p /app/App_Data && chown -R eminova:eminova /app
COPY --from=build --chown=eminova:eminova /app/publish .
USER eminova
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Hosting__HttpsRedirect=false
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Eminova.Host.dll"]
