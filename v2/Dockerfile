FROM node:22-bookworm-slim AS web
WORKDIR /src
COPY v2/web/package*.json ./
RUN npm ci
COPY v2/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY v2/TaskService/TaskService.csproj ./
RUN dotnet restore
COPY v2/TaskService/ ./
COPY --from=web /src/dist/ ./wwwroot/
RUN dotnet publish --no-restore -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -m 0700 -o app -g app /var/lib/taskservice/keys
COPY --from=build /out/ ./
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DataProtection__KeyDirectory=/var/lib/taskservice/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskService.dll"]
