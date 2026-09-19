# Production operations

The platform is fail-closed by default. External execution remains disabled until the global,
domain and provider controls are intentionally configured. Secrets must come from environment
variables or an external configuration provider; they do not belong in tracked settings files.

## Schema changes

Generate and review a migration for both provider-specific migration projects. Apply it to a
restored copy of production before applying it to production. Deploy additive schema changes
before the application version that uses them. Destructive changes require a separate backfill,
verification and removal release.

```powershell
dotnet ef database update -p src/AgentTrust.Data.Migrations.SqlServer -s src/AgentTrust.Data.Migrations.SqlServer
dotnet ef database update -p src/AgentTrust.Data.Migrations.Postgres -s src/AgentTrust.Data.Migrations.Postgres
```

The API runs migrations at startup when a database connection is configured. For a controlled
pilot, prefer applying reviewed migrations as a deployment step and use `/health/ready` to verify
that the system-of-record database is reachable.

## Backup and restore

1. Take an encrypted, provider-native full backup before every schema deployment.
2. Record the migration ID and application version with the backup.
3. Restore into an isolated database using a least-privilege restore identity.
4. Run `SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId` and the automated test
   suite against the restored database.
5. Verify counts for intents, executions, payment attempts, webhook events and audit records.
6. Point a non-production API instance at the restored database and verify `/health/ready`.

Never test a restore by overwriting the live database. Rollback means restoring the verified
backup or deploying a reviewed forward-fix; do not delete migration-history rows manually.

## Unknown outcomes

Provider timeouts after submission are stored as `Unknown`. The reconciliation worker queries the
provider using the stable intent/idempotency key. Operators must not create a replacement action
while reconciliation is pending. After bounded attempts the record remains visible for manual
review instead of being executed again.

## Key rotation

Rotate exposed webhook, payment and signing secrets at the provider first, update the external
secret store, restart the relevant instances, and verify signed requests. Signing-key rotation
needs an overlap period in which existing authorisations can still be verified by key ID.
