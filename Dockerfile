FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY AgenticPaymentTrust.sln .
COPY src/AgentTrust.Core/AgentTrust.Core.csproj src/AgentTrust.Core/
COPY src/AgentTrust.Policy/AgentTrust.Policy.csproj src/AgentTrust.Policy/
COPY src/AgentTrust.Payments/AgentTrust.Payments.csproj src/AgentTrust.Payments/
COPY src/AgentTrust.Evidence/AgentTrust.Evidence.csproj src/AgentTrust.Evidence/
COPY src/AgentTrust.Agents/AgentTrust.Agents.csproj src/AgentTrust.Agents/
COPY src/AgentTrust.Runner/AgentTrust.Runner.csproj src/AgentTrust.Runner/
COPY tests/AgentTrust.Tests/AgentTrust.Tests.csproj tests/AgentTrust.Tests/
RUN dotnet restore src/AgentTrust.Runner/AgentTrust.Runner.csproj

COPY src/ src/
COPY scenarios/ scenarios/
RUN dotnet publish src/AgentTrust.Runner/AgentTrust.Runner.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY scenarios/ scenarios/
RUN mkdir -p /app/results

ENTRYPOINT ["dotnet", "AgentTrust.Runner.dll"]
