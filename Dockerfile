# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY InatBestiary/InatBestiary.csproj InatBestiary/
RUN dotnet restore InatBestiary/InatBestiary.csproj

COPY InatBestiary/ InatBestiary/
RUN dotnet publish InatBestiary/InatBestiary.csproj \
      -c Release \
      -r win-x64 \
      --self-contained true \
      -p:DebugType=none \
      -p:DebugSymbols=false \
      -o /output

# ── Export stage ─────────────────────────────────────────────────────────────
# Extract the published output with:
#   docker build --target export --output type=local,dest=.\publish .
FROM scratch AS export
COPY --from=build /output /
