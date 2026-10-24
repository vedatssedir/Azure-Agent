# Roadmap

Status of the platform and the next steps, in order.

## Done (v0.1)

- Clean Architecture solution (.NET 10) with Wolverine mediator, FluentValidation, Carter, Serilog,
  OpenTelemetry, Redis, RabbitMQ, TickerQ, PostgreSQL.
- JWT auth with rotating refresh tokens, lockout, users and memberships in PostgreSQL.
- Multi-tenant organizations, project roles, fine-grained permissions, repository allow/deny rules,
  repository agent policies, encrypted secrets, audit log.
- Azure DevOps sync (teams, repos, commits, PRs, work items), Service Hooks, assignment in Azure.
- Developer agents (Microsoft Agent Framework) that implement work items and open pull requests;
  reviewer agents that review pull requests; skills, MCP servers, skill suggestions.
- Cost per model call and per run, cost reports, monthly budgets, per-run token and cost limits.
- React UI: board, work item detail, repositories (history, policy, access rules), pull requests,
  agents, runs (step timeline), costs and budgets, members, settings, audit, organization admin.

## Next

1. **Entra ID sign-in** (OIDC) next to local accounts; service principal / managed identity for
   Azure DevOps instead of PATs.
2. **Follow-up on PR feedback**: when reviewers comment or request changes, queue a follow-up run on
   the same branch.
3. **Pipelines**: read build/test results of the PR (Azure Pipelines) and let the agent fix failures.
4. **Sandboxed runners**: run agent workspaces in containers (Kubernetes jobs / ACI) with network
   policies; resource quotas per team.
5. **Notifications**: Teams/e-mail on run failure, budget thresholds (80 %/100 %), pending approvals.
6. **Analytics**: cost trend charts, cost per story point, success rate per agent/model, lead time
   from assignment to merged PR.
7. **Real-time UI** (SignalR) for run progress instead of polling.
8. **Operational hardening**: Wolverine durable outbox on by default, TickerQ EF operational store for
   multi-node scheduling, rate limits per organization, data retention policies.
