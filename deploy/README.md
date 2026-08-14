# Local Kubernetes deployment (Docker Desktop)

Runs the Read API and the Kafka consumer worker on the Docker Desktop Kubernetes
cluster. This is a local development convenience only — real deployment happens
outside this repository (see `AGENTS.md`).

Layout:

```
deploy/docker/api.Dockerfile        image for src/Clients/UBS.AM.PLT.Snapshot.Api
deploy/docker/consumer.Dockerfile   image for src/Clients/UBS.AM.PLT.Snapshot.Worker
deploy/helm/snapshot/               the chart: one template file per service
deploy/values.local.yaml            gitignored, holds the SQL connection string
```

## Where the dependencies live

Everything the pods talk to runs on the host, not in the cluster. Pods reach the host
through `host.docker.internal`, which Docker Desktop resolves from inside pods.

| Dependency | Runs as | Pods reach it at |
| --- | --- | --- |
| Kafka | host-provided broker | `host.docker.internal:9092` |
| Blob storage (Azurite) | host-provided emulator | `http://host.docker.internal:10000` |
| SQL Server | the host machine's own instance | `host.docker.internal,1433` |

### Host SQL Server from a container

A Linux container cannot use `Trusted_Connection` (Windows authentication), so the host
instance needs SQL authentication:

1. Mixed-mode authentication enabled
   (`EXEC xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'LoginMode', REG_DWORD, 2`),
   then restart the `MSSQLSERVER` service.
2. TCP/IP enabled on port 1433 (already the default on this machine) and reachable —
   Docker Desktop pods connect through the host gateway, so no extra firewall rule was needed.
3. A SQL login with `db_datareader` + `db_datawriter` on `platform-core-db-dev`.

The resulting connection string goes into `deploy/values.local.yaml`, which is gitignored:

```yaml
database:
  connectionString: "Server=host.docker.internal,1433;Database=platform-core-db-dev;User Id=<login>;Password=<password>;TrustServerCertificate=True;Encrypt=False"
```

## Dependency prerequisites

Kafka and Azurite must already be running on the host at the addresses above. Kafka
must advertise `host.docker.internal`, which resolves both on the host and from the pods.

## Build the images

Both build from the repository root as context. Docker Desktop's Kubernetes reads the
local image store directly, so no push or image load is needed.

```bash
docker build -f deploy/docker/api.Dockerfile -t ubs-snapshot-api:local .
```

```bash
docker build -f deploy/docker/consumer.Dockerfile -t ubs-snapshot-consumer:local .
```

## Deploy

```bash
helm upgrade --install snapshot deploy/helm/snapshot -n snapshot-local --create-namespace -f deploy/values.local.yaml --wait
```

Result: one API pod and three consumer pods (one per partition of the request topic).

```bash
kubectl -n snapshot-local get pods
```

The API is published on the host by Docker Desktop's load balancer:

```bash
curl http://localhost:8080/snapshots/api/health/live
```

A NodePort is not reachable from the host on the kind-based Docker Desktop cluster;
use the default `LoadBalancer` service type, or `kubectl -n snapshot-local port-forward svc/snapshot-api 8080:8080`.

## Configuration

Both services read the same ConfigMap and Secret. Keys are .NET configuration paths in
environment-variable form, so they override `appsettings.json` without a code change.
`BlobStorage__ServiceUri` is deliberately empty: an empty value makes the blob factory
use the connection string (Azurite) instead of `DefaultAzureCredential`, which pods do
not have. Kafka topic names stay in `appsettings.json` because the topic keys
(`snapshot-request`, `snapshot-response`) contain a dash, which is not a legal
environment-variable name.

## Remove

```bash
helm uninstall snapshot -n snapshot-local
```
