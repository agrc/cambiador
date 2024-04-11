FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS dotnet
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-sdk

FROM dotnet-sdk as build
COPY ./src ./build
WORKDIR /build
RUN dotnet build -c Release -o /app

FROM build as publish
RUN dotnet publish -c Release -o /app -r linux-x64 -p:PublishSingleFile=true --no-self-contained -p:DebugType=embedded -p:PublishReadyToRun=true

FROM dotnet AS final
WORKDIR /app
COPY --from=publish /app .

ENTRYPOINT ["dotnet", "cambiador.dll"]
