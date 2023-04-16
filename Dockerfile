FROM mcr.microsoft.com/dotnet/sdk:7.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish gpt.csproj -c Release -r linux-musl-x64 -o /app/publish --self-contained true -p:PublishSingleFile=true

FROM alpine
RUN apk add --no-cache libstdc++ libgcc icu-libs
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/appSettings.json .
ENV MODEL="gpt-3.5-turbo"
ENTRYPOINT ["./gpt", "discord", "--model=${MODEL}"]