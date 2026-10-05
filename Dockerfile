FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CandaceHolder/CandaceHolder.csproj CandaceHolder/
RUN dotnet restore CandaceHolder/CandaceHolder.csproj
COPY . .
RUN dotnet publish CandaceHolder/CandaceHolder.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
    fontconfig libfontconfig1 fonts-dejavu-core \
    gdal-bin \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CandaceHolder.dll"]
