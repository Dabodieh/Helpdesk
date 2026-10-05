FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Helpdesk.Host/Helpdesk.Host.csproj src/Helpdesk.Host/
RUN dotnet restore src/Helpdesk.Host
COPY src/Helpdesk.Host src/Helpdesk.Host
RUN dotnet publish src/Helpdesk.Host -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Helpdesk.Host.dll"]
