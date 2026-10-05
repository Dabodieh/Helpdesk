FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# Project files first so the restore layer is cached until a dependency changes.
COPY src/Helpdesk.SharedKernel/Helpdesk.SharedKernel.csproj src/Helpdesk.SharedKernel/
COPY src/Helpdesk.Modules.Audit/Helpdesk.Modules.Audit.csproj src/Helpdesk.Modules.Audit/
COPY src/Helpdesk.Modules.Identity/Helpdesk.Modules.Identity.csproj src/Helpdesk.Modules.Identity/
COPY src/Helpdesk.Modules.Organisation/Helpdesk.Modules.Organisation.csproj src/Helpdesk.Modules.Organisation/
COPY src/Helpdesk.Host/Helpdesk.Host.csproj src/Helpdesk.Host/
RUN dotnet restore src/Helpdesk.Host
COPY src/Helpdesk.SharedKernel src/Helpdesk.SharedKernel
COPY src/Helpdesk.Modules.Audit src/Helpdesk.Modules.Audit
COPY src/Helpdesk.Modules.Identity src/Helpdesk.Modules.Identity
COPY src/Helpdesk.Modules.Organisation src/Helpdesk.Modules.Organisation
COPY src/Helpdesk.Host src/Helpdesk.Host
RUN dotnet publish src/Helpdesk.Host -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Apply migrations with an explicit one-off run of the same image: `docker run ... helpdesk-host migrate`
ENTRYPOINT ["dotnet", "Helpdesk.Host.dll"]
