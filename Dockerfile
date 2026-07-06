FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy the whole context: needed because Server references other projects
# living at the repo root (ProjectReference paths must resolve during restore)
COPY . .

RUN dotnet restore Server/Server.csproj
RUN dotnet publish Server/Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN groupadd -r appuser && useradd -r -g appuser appuser
WORKDIR /app
COPY --from=build /app/publish .
RUN chown -R appuser:appuser /app
USER appuser

EXPOSE 8153
ENV DOTNET_gcServer=1
ENTRYPOINT ["dotnet", "Server.dll", "--port", "8153"]
