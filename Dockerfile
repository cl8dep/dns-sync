FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/DnsSync/DnsSync.csproj src/DnsSync/
RUN dotnet restore src/DnsSync/DnsSync.csproj

COPY src/ src/
RUN dotnet publish src/DnsSync \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app

RUN adduser --disabled-password --no-create-home --uid 1001 dnsync
USER 1001

COPY --from=build /app/publish/dns-sync ./dns-sync

ENTRYPOINT ["./dns-sync"]
