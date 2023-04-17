FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish gpt.csproj -c Release -r linux-x64 -o /app/publish --self-contained true -p:PublishSingleFile=true

FROM redhat/ubi9
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/appSettings.json .
ENV MODEL=""
ENTRYPOINT ["./gpt", "discord"]
