# f.commerce

B2B e-commerce platform built as a modular monolith on .NET 10.

## Getting started

```bash
docker compose up -d
dotnet run --project src/FCommerce.Host --launch-profile https
```

## Tests

```bash
dotnet test
```

Requires Docker (tests start PostgreSQL via Testcontainers).
