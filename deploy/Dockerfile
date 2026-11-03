FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ChurchTranslate.Api/ChurchTranslate.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "ChurchTranslate.Api.dll"]
