# Azure Agent

Azure DevOps-native platform where AI agents do the developers' work. Azure Boards stays the
source of truth: a developer assigns a work item to themselves, their paired agent clones the
repositories named on the item, implements the change, pushes a branch and opens a pull request
linked to the work item. Reviewer agents review pull requests. Every model call is metered, so
cost per job, per agent, per work item and per project is always known.

## Stack

| Area | Technology |
|---|---|
| Backend | .NET 10, ASP.NET Core minimal APIs with **Carter** modules, Clean Architecture |
| Mediator / messaging | **Wolverine** (`IMessageBus.InvokeAsync` in endpoints; cascading messages) over **RabbitMQ** |
| Validation | **FluentValidation** through Wolverine middleware |
| Data | **PostgreSQL** with EF Core 10 (Npgsql), multi-tenant query filters |
| Cache | **Redis** (`IDistributedCache`) |
| Schedules | **TickerQ** cron functions |
| Agents | **Microsoft Agent Framework** (`ChatClientAgent`) over Microsoft.Extensions.AI: OpenAI, Azure OpenAI, Anthropic, Gemini; MCP tools |
| Auth | JWT access tokens, rotating refresh tokens (reuse detection), BCrypt, lockout; users in PostgreSQL |
| Logging | **Serilog** (structured, console + Seq), OpenTelemetry traces/metrics (OTLP) |
| Frontend | React 19, Vite, TypeScript, TanStack Query, React Router |

## Solution layout

```
src/
  AzureAgent.Domain          Entities, roles and permissions, run state machine, cost model
  AzureAgent.Application     Commands/queries + Wolverine handlers + validators, access control, sync, dispatch, costs
  AzureAgent.Infrastructure  EF Core, security, Redis, Azure DevOps REST client, agent runtime, TickerQ jobs
  AzureAgent.Api             Carter modules, middleware, Program (Serilog, JWT, Wolverine, TickerQ, OTel)
web/                         React UI (built into src/AzureAgent.Api/wwwroot)
tests/
  AzureAgent.UnitTests
  AzureAgent.IntegrationTests  Real API + PostgreSQL + git, fake Azure DevOps and scripted model
```

## How the work flows

1. **Sync** – every project is reconciled on its interval (TickerQ → `SyncProject` on RabbitMQ):
   teams and members, repositories, commits (with `AB#123` links), pull requests and work items.
   Azure Service Hooks (`/api/hooks/{projectId}`) refresh items immediately.
2. **Dispatch** – an open work item assigned to a developer who has a paired agent gets one
   `Implement` run (optionally waiting for human approval). Repositories come from `repo:<name>` tags.
3. **Run** (`ExecuteAgentRun` on the `agent-runs` queue) – budget check → clone → branch `ab/<id>-<slug>`
   → Microsoft Agent Framework agent with workspace tools (`list_files`, `read_file`, `write_file`,
   `replace_in_file`, `search`, `run_command` limited to the repository's allowed commands) and the
   agent's skills and MCP servers → commit, push, pull request linked to the work item → comment and
   review state on the work item.
4. **Review** – new pull requests in repositories with auto-review are reviewed by the project's
   reviewer agent; findings are posted as PR threads and stored.
5. **Cost** – every model call records tokens and price (`ModelPrice`, per 1M tokens, organization
   overrides). Runs stop when token, cost-per-run or monthly budgets (organization, project, agent)
   are exceeded. Reports group cost by agent, work item, day, model or run kind.

## Security model

- **Tenancy**: organizations are tenants; every tenant row carries `OrganizationId` and EF query
  filters scope all reads; writes to another tenant are rejected. `X-Organization-Id` is verified
  against membership.
- **Roles**: organization (Owner, Admin, Member) and per-project roles (Viewer, Developer, Reviewer,
  Maintainer, Admin) map to fine-grained permissions (`Domain/Projects/Permissions.cs`).
- **Repository rules**: allow/deny a permission per repository for a user or a role (deny wins),
  e.g. deny `repo.agent.write` for developers on `billing`. Agents act with their paired developer's
  permissions.
- **Repository policy**: agents on/off, protected branches, allowed commands, max changed files,
  auto-review.
- **Secrets**: AES-256-GCM at rest, project or organization scope, never returned by the API; PATs
  travel to git as an environment-scoped `http.extraHeader`, never in argv or URLs.
- **Audit**: every mutation writes an audit entry.

## Run locally

```sh
docker compose up -d                 # PostgreSQL, Redis, RabbitMQ, Seq
cd web && npm ci && npm run build -- --outDir ../src/AzureAgent.Api/wwwroot && cd ..
dotnet run --project src/AzureAgent.Api   # Development settings: appsettings.Development.json
```

Open http://localhost:5080 (or the Vite dev server: `cd web && npm run dev` → http://localhost:5173),
choose **İlk kurulum**, create a project, set the `AZURE_DEVOPS_PAT` and a model API key secret,
then **Ayarlar → Şimdi senkronla** and **Agent'lar → Eksik agent'ları oluştur**.

Production configuration (environment variables):

| Setting | Purpose |
|---|---|
| `ConnectionStrings__Postgres` / `__Redis` / `__RabbitMq` | Infrastructure |
| `Jwt__SigningKey` | ≥ 32 characters |
| `Encryption__Key` | base64 32-byte key (`openssl rand -base64 32`); back it up |
| `Messaging__AgentWorkers` | Parallel agent runs per node (0 = API-only node) |
| `Messaging__Durable` | Wolverine PostgreSQL outbox for durable messaging |
| `Seq__Url`, `OpenTelemetry__Endpoint` | Observability |
| `AgentRuntime__WorkspaceRoot` | Scratch space for clones |

## Tests

```sh
dotnet test --project tests/AzureAgent.UnitTests
TEST_POSTGRES="Host=localhost;Database=azure_agent_test;Username=azure_agent;Password=azure_agent" \
  dotnet test --project tests/AzureAgent.IntegrationTests
```

The integration suite runs the real API against PostgreSQL and real git repositories with an
in-memory Azure DevOps project and a scripted model: assignment → agent run → push → PR → cost,
role and repository permissions, tenant isolation, budgets and token limits, AI review, Service Hooks.

See [docs/ROADMAP.md](docs/ROADMAP.md) for the next steps.
