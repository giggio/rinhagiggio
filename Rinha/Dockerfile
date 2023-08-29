FROM mcr.microsoft.com/dotnet/runtime-deps:8.0.0-preview.7-jammy as base
WORKDIR /app
ENV ASPNETCORE_URLS http://*:9999
RUN apt-get update && apt-get install curl -y
ENTRYPOINT ["/app/Rinha"]

FROM mcr.microsoft.com/dotnet/sdk:8.0.100-preview.7-jammy AS build
WORKDIR /app
COPY ./Rinha/Rinha.csproj ./Rinha/
RUN cd Rinha && ls -lah && dotnet restore /property:Configuration=Release
COPY . .
RUN cd Rinha && dotnet publish -c Release -r linux-x64 -o out

FROM base
COPY --from=build /app/Rinha/out .