// Typed API client. The access token lives in memory; the refresh token in localStorage so a
// reload can resume the session. A 401 triggers one refresh attempt, then sign-out.

const REFRESH_KEY = "azure-agent.refresh";
const ORG_KEY = "azure-agent.organization";

function read(key: string) {
  try { return localStorage.getItem(key); } catch { return null; }
}
function write(key: string, value: string | null) {
  try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value); } catch { /* storage unavailable */ }
}

let accessToken: string | null = null;
let refreshing: Promise<boolean> | null = null;
const listeners = new Set<() => void>();

export const session = {
  get organizationId() { return read(ORG_KEY); },
  set organizationId(value: string | null) { write(ORG_KEY, value); },
  get signedIn() { return accessToken !== null || read(REFRESH_KEY) !== null; },
  onSignOut(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  store(result: AuthResult) {
    accessToken = result.accessToken;
    write(REFRESH_KEY, result.refreshToken);
  },
  async signOut() {
    const refreshToken = read(REFRESH_KEY);
    accessToken = null;
    write(REFRESH_KEY, null);
    if (refreshToken) await fetch("/api/auth/logout", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ refreshToken }) }).catch(() => undefined);
    listeners.forEach((l) => l());
  },
};

async function refresh(): Promise<boolean> {
  const refreshToken = read(REFRESH_KEY);
  if (!refreshToken) return false;
  const response = await fetch("/api/auth/refresh", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ refreshToken }) });
  if (!response.ok) return false;
  session.store(await response.json());
  return true;
}

export class ApiError extends Error {
  constructor(readonly status: number, message: string, readonly errors?: Record<string, string[]>) { super(message); }
}

export async function api<T = void>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const send = () => fetch(path, {
    method: init.method ?? (init.body !== undefined ? "POST" : "GET"),
    headers: {
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...(session.organizationId ? { "X-Organization-Id": session.organizationId } : {}),
      ...(init.body !== undefined ? { "Content-Type": "application/json" } : {}),
    },
    body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  if (!accessToken && read(REFRESH_KEY) && !path.startsWith("/api/auth/")) {
    refreshing ??= refresh().finally(() => { refreshing = null; });
    await refreshing;
  }
  let response = await send();
  if (response.status === 401 && !path.startsWith("/api/auth/login")) {
    refreshing ??= refresh().finally(() => { refreshing = null; });
    if (await refreshing) response = await send();
    else { await session.signOut(); throw new ApiError(401, "Oturum süresi doldu"); }
  }
  if (response.status === 204) return undefined as T;
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const problem = data as { title?: string; errors?: Record<string, string[]> };
    const detail = problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
    const title = response.status === 429 ? "Çok fazla istek; biraz sonra tekrar deneyin" : problem.title ?? `HTTP ${response.status}`;
    throw new ApiError(response.status, [title, detail].filter(Boolean).join(": "), problem.errors);
  }
  return data as T;
}

export const p = (projectId: string, path = "") => `/api/projects/${projectId}${path}`;

// Types (mirror the API DTOs) ---------------------------------------------------------------

export type OrganizationRole = "Member" | "Admin" | "Owner";
export type ProjectRole = "Viewer" | "Developer" | "Reviewer" | "Maintainer" | "Admin";
export type RunStatus = "PendingApproval" | "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";
export type ModelProvider = "OpenAI" | "AzureOpenAI" | "Anthropic" | "Gemini";
export type AgentKind = "Developer" | "Reviewer";

export interface AuthResult { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; user: User }
export interface User { id: string; email: string; displayName: string; isSystemAdmin: boolean; hasPassword: boolean }
export interface Me { user: User; organizations: Array<{ organizationId: string; name: string; slug: string; role: OrganizationRole }> }

export interface TeamRoleMapping { team: string; role: ProjectRole }
export interface GroupRoleMapping { groupId: string; role: ProjectRole }
export interface ProjectSettings {
  autoDispatch: boolean; requireRunApproval: boolean; reviewState: string | null; closedStates: string[];
  autoReviewPullRequests: boolean; syncIntervalMinutes: number;
  autoProvisionMembers: boolean; autoInviteTeamMembers: boolean; requireUserCredentialForWrites: boolean;
  teamRoles: TeamRoleMapping[]; groupRoles: GroupRoleMapping[];
}
export interface Project {
  id: string; name: string; key: string; description: string | null; azureOrganizationUrl: string; azureProjectName: string;
  settings: ProjectSettings; monthlyBudgetUsd: number | null; lastSyncedAt: string | null; lastSyncError: string | null; myPermissions: string[];
}
export interface WorkItem {
  azureId: number; type: string; title: string; state: string; assignedToUniqueName: string | null; assignedToDisplayName: string | null;
  priority: number | null; storyPoints: number | null; repositoryNames: string[]; iterationPath: string | null; webUrl: string; changedAt: string;
  lastRunStatus: RunStatus | null; agentName: string | null; costUsd: number;
}
export interface RunSummary {
  id: string; kind: "Implement" | "Review"; status: RunStatus; agentName: string; workItemAzureId: number | null; branch: string | null;
  pullRequestUrls: string[]; error: string | null; inputTokens: number; outputTokens: number; costUsd: number; createdAt: string;
  startedAt: string | null; finishedAt: string | null;
}
export interface PullRequest {
  id: string; azureId: number; repository: string; title: string; status: "Active" | "Completed" | "Abandoned"; sourceBranch: string; targetBranch: string;
  createdBy: string; createdByAgent: boolean; openedAt: string; closedAt: string | null; webUrl: string; workItemAzureIds: number[];
}
export interface Commit { commitId: string; repository: string; authorName: string; message: string; committedAt: string; webUrl: string }
export interface WorkItemDetail {
  summary: WorkItem; description: string; acceptanceCriteria: string; areaPath: string | null; tags: string[];
  runs: RunSummary[]; pullRequests: PullRequest[]; commits: Commit[];
}
export interface RepositoryPolicy { agentsEnabled: boolean; protectedBranches: string[]; autoReview: boolean; allowedCommands: string[]; maxFilesChangedPerRun: number }
export interface Repository { id: string; name: string; defaultBranch: string | null; webUrl: string; policy: RepositoryPolicy; openPullRequests: number; lastCommitAt: string | null }
export interface AccessRule { id: string; userId: string | null; role: ProjectRole | null; permission: string; effect: "Allow" | "Deny" }
export interface Team { id: string; name: string; members: Array<{ uniqueName: string; displayName: string; agentId: string | null; agentName: string | null }>; areaPaths: string[] }
export interface AgentSettings { temperature: number | null; maxIterations: number; maxTokensPerRun: number; maxCostPerRunUsd: number; timeoutMinutes: number; endpoint: string | null }
export interface Agent {
  id: string; name: string; kind: AgentKind; provider: ModelProvider; model: string; instructions: string; pairedUniqueName: string | null; teamId: string | null;
  enabled: boolean; maxConcurrentRuns: number; monthlyBudgetUsd: number | null; settings: AgentSettings; skills: string[]; mcpServers: string[];
  activeRuns: number; spentThisMonthUsd: number;
}
export interface RunStep { sequence: number; type: "Log" | "ModelCall" | "ToolCall" | "Git" | "Error"; title: string; detail: string | null; inputTokens: number; outputTokens: number; costUsd: number; durationMs: number; createdAt: string }
export interface RunDetail { run: RunSummary; summary: string | null; provider: string | null; model: string | null; modelCalls: number; toolCalls: number; cachedInputTokens: number; requestedBy: string | null; approvedBy: string | null; steps: RunStep[] }
export interface CostRow { key: string; label: string; runs: number; succeededRuns: number; inputTokens: number; outputTokens: number; costUsd: number; averageCostPerRunUsd: number; averageDurationMinutes: number }
export interface CostReport { from: string; to: string; groupBy: string; totalCostUsd: number; totalTokens: number; totalRuns: number; costPerSucceededRunUsd: number; rows: CostRow[] }
export interface BudgetLine { scope: string; name: string; limitUsd: number | null; spentUsd: number; usedPercent: number | null }
export type MembershipSource = "Manual" | "AzureTeam" | "EntraGroup";
export interface Member { userId: string; email: string; displayName: string; role: ProjectRole; azureUniqueName: string | null; source: MembershipSource }
export interface ProjectPerson {
  uniqueName: string; displayName: string; teams: string[]; mappedRole: ProjectRole | null; userId: string | null; role: ProjectRole | null;
  source: MembershipSource | null; azureConnected: boolean; invitationStatus: InvitationStatus | null; agentName: string | null;
}
export type InvitationStatus = "Pending" | "Accepted" | "Expired" | "Revoked";
export interface InvitationLink { id: string; email: string; url: string; emailSent: boolean }
export interface Invitation { id: string; email: string; displayName: string; role: OrganizationRole; status: InvitationStatus; expiresAt: string; createdAt: string; invitedBy: string; projectId: string | null }
export interface InvitationInfo { email: string; displayName: string; organizationName: string; expiresAt: string; hasAccount: boolean; ssoEnabled: boolean }
export interface AuthProviders { local: boolean; entra: boolean; entraName: string | null }
export interface AzureCredential { id: string; kind: "PersonalAccessToken" | "OAuth"; azureOrganizationUrl: string | null; azureUniqueName: string; azureDisplayName: string | null; verifiedAt: string; expiresAt: string | null; lastError: string | null }
export interface WorkItemType { name: string; states: string[]; fields: string[] }
export interface ProvisioningResult { added: number; updated: number; removed: number; invited: number }
export interface OrgMember { userId: string; email: string; displayName: string; role: OrganizationRole; isActive: boolean }
export interface Secret { name: string; projectId: string | null; updatedAt: string }
export interface Skill { id: string; key: string; name: string; description: string; content: string }
export interface McpServer { id: string; name: string; transport: "Stdio" | "Http"; command: string | null; arguments: string[]; url: string | null; secretNames: string[] }
export interface Catalog { skills: Array<Omit<Skill, "id"> & { signals: string[] }>; mcpServers: Array<{ name: string; description: string; transport: string; command: string | null; arguments: string[]; url: string | null; secretNames: string[] }> }
export interface ModelPrice { id: string; isDefault: boolean; provider: ModelProvider; model: string; inputPerMillion: number; outputPerMillion: number; cachedInputPerMillion: number }
export interface AuditEntry { at: string; actor: string; action: string; entityType: string; entityId: string; details: string | null }

// Formatting ------------------------------------------------------------------------------------

export const fmtDate = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString("tr-TR", { dateStyle: "short", timeStyle: "short" }) : "—";
export const fmtUsd = (value: number | null | undefined, digits = 2) =>
  value === null || value === undefined ? "—" : `$${value.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: Math.max(digits, 4) })}`;
export const fmtTokens = (value: number) => value >= 1_000_000 ? `${(value / 1_000_000).toFixed(2)}M` : value >= 1_000 ? `${(value / 1_000).toFixed(1)}k` : `${value}`;

export const RUN_STATUS: Record<RunStatus, { label: string; cls: string }> = {
  PendingApproval: { label: "Onay bekliyor", cls: "warn" },
  Queued: { label: "Sırada", cls: "warn" },
  Running: { label: "Çalışıyor", cls: "info" },
  Succeeded: { label: "Tamamlandı", cls: "ok" },
  Failed: { label: "Başarısız", cls: "bad" },
  Cancelled: { label: "İptal", cls: "" },
};

export const SOURCE_LABEL: Record<MembershipSource, string> = { Manual: "Elle", AzureTeam: "Azure takımı", EntraGroup: "Entra grubu" };
export const INVITATION_LABEL: Record<InvitationStatus, { label: string; cls: string }> = {
  Pending: { label: "Davet bekliyor", cls: "warn" }, Accepted: { label: "Kabul edildi", cls: "ok" }, Expired: { label: "Süresi doldu", cls: "bad" }, Revoked: { label: "İptal", cls: "" },
};

export const ROLE_LABEL: Record<ProjectRole, string> = { Viewer: "İzleyici", Developer: "Developer", Reviewer: "Reviewer", Maintainer: "Maintainer", Admin: "Proje yöneticisi" };
