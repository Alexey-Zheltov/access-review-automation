# Multi-stage build. Targets:
#   runtime  - the portal (default). Works on Docker hosts, Azure App Service (Web App for Containers) and Azure Container Apps.
#   migrator - EF Core migration bundle (efbundle). Creates or updates the database schema; safe to run many times.
#   test     - runs the unit tests (docker build --target test .).
# Image tags can be pinned with --build-arg, e.g. SDK_TAG=10.0.403 RUNTIME_TAG=10.0.14.
ARG SDK_TAG=10.0
ARG RUNTIME_TAG=10.0

FROM mcr.microsoft.com/dotnet/sdk:${SDK_TAG} AS build
WORKDIR /src
# Directory.Build.props: NuGet audit, the restore stops on packages with known high or critical vulnerabilities.
COPY dotnet-tools.json Directory.Build.props ./
COPY src/AccessReviewPortal/AccessReviewPortal.csproj src/AccessReviewPortal/
RUN dotnet tool restore && dotnet restore src/AccessReviewPortal/AccessReviewPortal.csproj
COPY src/ src/
RUN dotnet publish src/AccessReviewPortal/AccessReviewPortal.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false \
    && test -f /app/publish/wwwroot/_framework/blazor.web.js

FROM build AS test
COPY tests/ tests/
RUN dotnet test tests/AccessReviewPortal.Tests/AccessReviewPortal.Tests.csproj -c Release

FROM build AS bundle
RUN dotnet tool run dotnet-ef migrations bundle --project src/AccessReviewPortal/AccessReviewPortal.csproj \
        --configuration Release --self-contained --target-runtime linux-x64 --output /bundle/efbundle --force

FROM mcr.microsoft.com/dotnet/runtime-deps:${RUNTIME_TAG} AS migrator
WORKDIR /bundle
# libgssapi-krb5-2 removes a noisy Npgsql message about missing Kerberos libraries.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
COPY --from=bundle /bundle/efbundle ./efbundle
USER $APP_UID
# The connection string comes from ConnectionStrings__DefaultConnection (or pass --connection "...").
ENTRYPOINT ["/bundle/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_TAG} AS runtime
WORKDIR /app
# 8080 = HTTP. HTTPS (8443) is switched on with ASPNETCORE_HTTPS_PORTS and a certificate, see docker-compose.yml.
# Data Protection keys and the MSAL token cache are stored in PostgreSQL, so sign-ins survive restarts.
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
# libgssapi-krb5-2 removes a noisy Npgsql start-up message about missing Kerberos libraries.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "AccessReviewPortal.dll"]
