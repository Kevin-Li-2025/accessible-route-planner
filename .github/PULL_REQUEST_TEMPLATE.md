## Scope

- [ ] Backend/API
- [ ] Frontend/mobile
- [ ] Routing/performance
- [ ] AI/planning intelligence
- [ ] Infrastructure/CI/CD
- [ ] Documentation only

## Evidence

Include the commands or artifacts that prove this change:

```text
dotnet build CodeConquerors.sln --configuration Release --no-restore
dotnet test AccessCity.Tests/AccessCity.Tests.csproj --configuration Release --no-build
```

For performance or AI changes, link the generated artifact:

- Benchmark/report path:
- p95/p99 or model metric delta:
- Known limitation:

## Safety Checklist

- [ ] No secrets, tokens, credentials, or private endpoint values are committed.
- [ ] Routing cost or graph behavior changes are covered by tests or benchmark evidence.
- [ ] AI output remains review-only and does not mutate route costs or map data without verification.
- [ ] Production claims are scoped to the exact benchmark environment and dataset.
