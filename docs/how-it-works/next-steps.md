# Next Steps

Current status: Phases 0–7 complete, Phase 8 (Service Bus migration, offline detection, simulator rewrite) complete. 150 unit tests + 4 integration tests passing. Notifications wired (SendGrid + Twilio).

---

## 1. CI/CD Pipeline

- GitHub Actions or Azure DevOps workflow: restore → build → test → publish
- Gate on all unit tests passing
- Optionally gate on integration tests with a Key Vault–connected runner
- Automated deployment to Azure (App Service or Container Apps)

## 2. Azure Deployment

- Deploy API + workers to Azure App Service or Container Apps
- Deploy Ingestion worker as a separate host
- Switch from connection strings to **Managed Identity** everywhere (SQL, Service Bus, Blob, IoT Hub, Key Vault)
- Configure staging and production environments (slots or separate resource groups)

## 3. Observability

- Wire up Application Insights / OpenTelemetry for distributed tracing across API → Service Bus → workers
- Dashboards: ingestion lag, alarm counts, command success rates, offline device count
- Alerts: ingestion stall, worker crash loops, high alarm rate, Service Bus dead-letter depth

## 4. Production Hardening

- Health checks — `/health` endpoint that probes SQL, Service Bus, IoT Hub, Blob
- Rate limiting on public-facing endpoints
- CORS locked to frontend origin
- Retry policies with Polly on transient failures (SQL, Service Bus, HTTP)
- Dead-letter queue monitoring and reprocessing strategy

## 5. Notification Policy Finalization (Phase 6 Gaps)

- Define escalation rules (who gets notified, after how long)
- Quiet hours / maintenance window suppression
- Retry schedule tuning
- Acknowledgment stops escalation

## 6. Phase 7 Hardening

- Azure Storage lifecycle policy (Hot → Cool → Archive tiers)
- Blob orphan cleanup during SQL retention purge
- Evaluate SQL table partitioning if telemetry volume grows

## 7. Security Review

- Tenant isolation audit (verify global query filters cannot be bypassed)
- Input validation on all endpoints
- Ensure no over-posting (DTO binding only)
- Secrets rotation strategy

## 8. Load Testing

- Simulate realistic device fleet (hundreds/thousands) hitting IoT Hub
- Measure ingestion throughput, DB write latency, alarm processing time
- Identify bottlenecks and tune (connection pooling, batch sizes, partition counts)
