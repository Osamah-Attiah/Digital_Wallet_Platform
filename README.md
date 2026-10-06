# Digital Wallet Platform

A demonstration digital wallet platform built with .NET 10, PostgreSQL, and a React administration interface. It uses synthetic data and balances only. Financial operations use a double-entry ledger, idempotency, atomic commits, and an outbox/inbox pattern. Administrative workflows separate maker and checker responsibilities and record audit trails.

Current status: **Phases 1–13 are documented. Phase 14 has completed local cloud-readiness checks; cloud deployment is pending explicit approval and actual cloud validation.** Source code has been published to GitHub. The project makes no claim of SAMA, PCI, or ISO compliance without a formal audit.

## Local setup

Requirements:

- Docker with Docker Compose.
- .NET SDK **10.0.400**, pinned in `global.json`.
- Node.js **24.19.0**, pinned in `.nvmrc`.

Initialize local secrets, prepare release metadata, and start the application with its observability services:

```sh
python3 ops/scripts/init-local.py
python3 ops/scripts/release.py
docker compose --profile observability build
docker compose --profile observability up -d --wait --wait-timeout 240
```

- Admin interface: <http://localhost:8081>
- API: <http://localhost:8080>

Local secrets are generated in `.local/secrets`, which is excluded from Git. Development fixtures are intended for local use and are excluded from the cloud production configuration. Integration tests require a Docker-compatible daemon, or an explicitly configured test server; fault scenarios should run only in an isolated development environment.

## Build and validation

Restore the locked dependencies, build the backend, and run the backend and frontend checks:

```sh
dotnet restore --locked-mode
dotnet build Digital_Wallet_Platform.sln -c Release --no-restore -warnaserror
dotnet test Digital_Wallet_Platform.sln -c Release --no-build
npm ci --ignore-scripts --prefix apps/admin
npm test --prefix apps/admin -- --run
npm run typecheck --prefix apps/admin
npm run build --prefix apps/admin
```

With the local Compose environment running, validate migrations, run the smoke scenarios, and scan the application images:

```sh
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/migration-tests.py
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/smoke.py
WALLET_EVIDENCE_DIR=ops/validation/phase14 python3 ops/scripts/scan.py
```

## Documentation and evidence

Detailed architecture documents, deployment guides, runbooks, and Markdown acceptance reports are retained locally. Only this root `README.md` is published as Markdown. The repository includes the following recorded validation evidence:

- [Phase 14 readiness and acceptance status](ops/validation/phase14/acceptance.json) and [historical Phase 13 acceptance](ops/validation/phase13/acceptance.json).
- [Backend validation](ops/validation/phase14/backend.json) and [frontend validation](ops/validation/phase14/frontend.json).
- [Migration validation](ops/validation/phase14/migrations.json) and [local smoke scenarios](ops/validation/phase14/smoke.json).
- [Application image security](ops/validation/phase14/security-summary.json), [infrastructure image security](ops/validation/phase14/infrastructure-security.json), and [migrator platform validation](ops/validation/phase14/migrator-platform.json).
- [Local backup and restore](ops/validation/phase14/backup-restore-local.json), [synthetic demo seed](ops/validation/phase14/synthetic-seed.json), and [local monitoring](ops/validation/phase14/monitoring-local.json).
- [Static cloud runtime contract](ops/validation/phase14/runtime-contract.json), [offline infrastructure plan](ops/validation/phase14/iac-plan.txt), and [cost ledger](ops/validation/phase14/cost-ledger.csv).

## Cloud readiness

The proposed deployment uses GCP with one `e2-standard-2` VM, a separate data disk, private networks, TLS, Secret Manager, scoped IAM permissions, and Prometheus/Grafana/Alertmanager. Terraform and its provider versions are pinned.

The [review plan](ops/validation/phase14/iac-plan.txt) uses placeholder identities and an empty state. It creates no resources and is **not ready to apply**. A live plan must be prepared after the project, region, and budget are approved, with checks for the operating system image, quotas, and identity configuration.

Cloud publication and deployment workflows run manually through protected environments, using OIDC, signed image digests, and scan attestations. Publication is blocked by High or Critical vulnerabilities or image secrets. Workflow YAML does not configure GitHub environment protections by itself; those protections require separate setup.

Cloud resource creation, registry publication, domain binding, and external alerts still require explicit authorization. Cloud checks remain **Skipped** until they are actually executed. Successful local Docker validation does not establish cloud acceptance.
