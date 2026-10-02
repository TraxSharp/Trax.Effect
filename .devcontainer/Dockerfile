FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:83e0db97c45d2e39b80123fe42940a23c423405a17f80b608a4b8768033d6392
RUN apt-get update && apt-get install -y git
RUN dotnet tool install --global dotnet-ef
ENV PATH="$PATH:/root/.dotnet/tools"
