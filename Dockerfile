FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-sdk
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS dotnet

FROM dotnet-sdk AS build
COPY ./src ./build
WORKDIR /build
RUN dotnet build -c Release -o /app

FROM build AS publish
RUN dotnet publish -c Release -o /app -r linux-x64 -p:PublishSingleFile=true --no-self-contained -p:DebugType=embedded -p:PublishReadyToRun=true

FROM dotnet AS final
WORKDIR /app
COPY --from=publish /app .

ENTRYPOINT ["dotnet", "cambiador.dll"]
