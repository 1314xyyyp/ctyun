FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base

WORKDIR /app

COPY ./publish/ ./

ENV CTYUN_DATA_DIR=/app/data

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "CtYun.dll"]
