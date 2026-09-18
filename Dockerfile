FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CvManager/CvManager.csproj CvManager/
RUN dotnet restore CvManager/CvManager.csproj
COPY CvManager/ CvManager/
RUN dotnet publish CvManager/CvManager.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "CvManager.dll"]
