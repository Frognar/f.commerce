# f.commerce

B2B e-commerce platform built as a modular monolith on .NET 10.

## Getting started

```bash
docker compose up -d
dotnet run --project src/FCommerce.Host --launch-profile https
```

Demo data (catalog, customers, documents) through the integration API; invitation e-mails arrive in Mailpit
(http://localhost:8025):

```bash
dotnet run --project tools/FCommerce.DemoSeeder
```

The seeder's output explains the demo, including the public API scenario (`api <customer key>`: catalog → cart →
order → the ERP confirms, delivers, invoices); API docs are served at `/openapi/public-v1.json`.

## Tests

```bash
dotnet test
```

Requires Docker (tests start PostgreSQL via Testcontainers).
