FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src/dotnet
COPY dotnet/ ./
RUN dotnet restore Rcm.Host --locked-mode
RUN dotnet publish Rcm.Host --no-restore -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS base
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Demo ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

FROM base AS runtime
COPY --from=build /out/ ./
COPY demo/appsettings.Demo.json ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Rcm.Host.dll"]

FROM base AS bootstrap
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/bootstrap
COPY requirements.txt /src/requirements.txt
RUN /opt/bootstrap/bin/pip install --no-cache-dir -r /src/requirements.txt
COPY --from=build /out/ /app/
COPY demo/appsettings.Demo.json /app/
COPY backend/ /src/backend/
COPY migrations/ /src/migrations/
COPY alembic.ini /src/
COPY scripts/configure-*.py /src/scripts/
COPY demo/ /src/demo/
WORKDIR /src
ENV PYTHONPATH=/src/backend
ENTRYPOINT ["/opt/bootstrap/bin/python", "/src/demo/bootstrap.py"]
