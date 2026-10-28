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
export const qs = (params: Record<string, string | number | boolean | null | undefined>) => {
  const search = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== null && v !== undefined && v !== "" && v !== false).map(([k, v]) => [k, String(v)])).toString();
  return search ? `?${search}` : "";
};

// Types (mirror the API DTOs) ---------------------------------------------------------------

export type OrganizationRole = "Member" | "Admin" | "Owner";
export type ProjectRole = "Viewer" | "Developer" | "Reviewer" | "Maintainer" | "Admin";
export type RunStatus = "PendingApproval" | "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";
export type ModelProvider = "OpenAI" | "AzureOpenAI" | "Anthropic" | "Gemini";
export type AgentKind = "Developer" | "Reviewer";
export type AgentRuntime = "Framework" | "ClaudeCode" | "CursorCli";
export type MembershipSource = "Manual" | "AzureTeam" | "EntraGroup";
export type InvitationStatus = "Pending" | "Accepted" | "Expired" | "Revoked";
export type PullRequestStatus = "Active" | "Completed" | "Abandoned";
export type ReviewVerdict = "Approve" | "Comment" | "RequestChanges";

export interface AuthResult { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; user: User }
export interface User { id: string; email: string; displayName: string; isSystemAdmin: boolean; hasPassword: boolean }
export interface Me { user: User; organizations: Array<{ organizationId: string; name: string; slug: string; role: OrganizationRole }> }
export interface SetupStatus { setupRequired: boolean; selfSignUp: boolean }
export interface AuthProviders { local: boolean; entra: boolean; entraName: string | null }
export interface InvitationInfo { email: string; displayName: string; organizationName: string; expiresAt: string; hasAccount: boolean; ssoEnabled: boolean }

export interface Project {
  id: string; name: string; key: string; description: string | null; azureProjectName: string; webUrl: string; lastSyncedAt: string | null; lastSyncError: string | null;
  myRole: ProjectRole | null; myPermissions: string[]; openWorkItems: number; assignedToMe: number; repositories: number; teams: number; monthlyBudgetUsd: number | null;
}
export interface Iteration { id: string; name: string; path: string; startDate: string | null; finishDate: string | null; timeFrame: string }
export interface Team { id: string; name: string; members: Array<{ uniqueName: string; displayName: string }>; areaPaths: string[]; boards: string[]; iterations: Iteration[] }
export interface WorkItemState { name: string; color: string; category: string }
export interface WorkItemType { name: string; color: string; states: WorkItemState[]; hasAcceptanceCriteria: boolean }
export interface WorkItem {
  azureId: number; type: string; typeColor: string; title: string; state: string; stateColor: string; stateCategory: string; boardColumn: string | null;
  assignedToUniqueName: string | null; assignedToDisplayName: string | null; priority: number | null; storyPoints: number | null; tags: string[];
  repositoryNames: string[]; areaPath: string | null; iterationPath: string | null; webUrl: string; changedAt: string; lastRunStatus: RunStatus | null;
  agentName: string | null; costUsd: number;
}
export interface MyWorkItem { projectId: string; projectName: string; item: WorkItem }
export interface BoardColumn { name: string; columnType: string; itemLimit: number; stateMappings: Record<string, string>; items: WorkItem[] }
export interface Board { teamId: string; teamName: string; board: string; boards: string[]; columns: BoardColumn[] }
export interface Sprint { teamId: string; teamName: string; iterations: Iteration[]; iteration: Iteration | null; items: WorkItem[] }
export interface RunSummary {
  id: string; kind: "Implement" | "Review"; status: RunStatus; agentId: string; agentName: string; projectId: string; workItemAzureId: number | null; branch: string | null;
  pullRequestUrls: string[]; error: string | null; inputTokens: number; outputTokens: number; costUsd: number; createdAt: string; startedAt: string | null; finishedAt: string | null;
}
export interface PullRequest {
  id: string; azureId: number; projectId: string; repositoryId: string; repository: string; title: string; status: PullRequestStatus; sourceBranch: string;
  targetBranch: string; createdBy: string; createdByAgent: boolean; openedAt: string; closedAt: string | null; webUrl: string; workItemAzureIds: number[];
  lastReviewVerdict: ReviewVerdict | null;
}
export interface CommitLink { commitId: string; repositoryId: string; repository: string; authorName: string; message: string; committedAt: string; webUrl: string }
export interface WorkItemDetail {
  summary: WorkItem; description: string; acceptanceCriteria: string; createdBy: string | null; createdAt: string | null; states: string[];
  runs: RunSummary[]; pullRequests: PullRequest[]; commits: CommitLink[];
}
export interface Comment { id: number; text: string; authorDisplayName: string; authorUniqueName: string; createdDate: string }

export interface RepositoryPolicy { agentsEnabled: boolean; protectedBranches: string[]; autoReview: boolean; allowedCommands: string[]; maxFilesChangedPerRun: number }
export interface Repository {
  id: string; projectId: string; projectName: string; name: string; defaultBranch: string | null; webUrl: string; sizeBytes: number; mirrorSyncedAt: string | null;
  mirrorError: string | null; openPullRequests: number; policy: RepositoryPolicy;
}
export interface GitBranch { name: string; commitId: string; committedAt: string; subject: string }
export interface RepositoryDetail { repository: Repository; branches: GitBranch[]; canChangePolicy: boolean }
export interface TreeEntry { name: string; path: string; isDirectory: boolean; size: number }
export interface Blob { path: string; size: number; isBinary: boolean; isTruncated: boolean; content: string | null }
export interface GitCommit { commitId: string; authorName: string; authorEmail: string; authoredAt: string; subject: string; message: string; parentIds: string[] }
export interface FileChange { path: string; oldPath: string | null; status: string; additions: number; deletions: number; patch: string; isBinary: boolean }
export interface CommitDetail { commit: GitCommit; files: FileChange[]; isTruncated: boolean }
export interface ReviewFinding { file: string; line: number | null; severity: string; message: string }
export interface Review { runId: string; agentName: string; verdict: ReviewVerdict; summary: string; findings: ReviewFinding[]; sourceCommit: string; createdAt: string }
export interface PullRequestDetail { pullRequest: PullRequest; reviews: Review[]; runs: RunSummary[]; files: FileChange[] }
export interface AccessRule { id: string; userId: string | null; userName: string | null; role: ProjectRole | null; permission: string; effect: "Allow" | "Deny" }

export interface AgentSkill { name: string; content: string; enabled: boolean }
export interface AgentSettings { temperature: number | null; maxIterations: number; maxTokensPerRun: number; maxCostPerRunUsd: number; timeoutMinutes: number; endpoint: string | null }
export interface Agent {
  id: string; name: string; slug: string; title: string | null; kind: AgentKind; runtime: AgentRuntime; provider: ModelProvider; model: string; instructions: string;
  ownerUserId: string | null; ownerName: string | null; ownerEmail: string | null; projectId: string | null; projectName: string | null; isDefault: boolean;
  enabled: boolean; maxConcurrentRuns: number; monthlyBudgetUsd: number | null; settings: AgentSettings; skills: AgentSkill[]; activeRuns: number;
  runsThisMonth: number; spentThisMonthUsd: number; lastRunAt: string | null; canEdit: boolean;
}
export interface RunStep { sequence: number; type: "Log" | "ModelCall" | "ToolCall" | "Git" | "Error"; title: string; detail: string | null; inputTokens: number; outputTokens: number; costUsd: number; durationMs: number; createdAt: string }
export interface RunDetail {
  run: RunSummary; projectName: string; workItemTitle: string | null; summary: string | null; provider: string | null; model: string | null; modelCalls: number;
  toolCalls: number; cachedInputTokens: number; requestedBy: string | null; approvedBy: string | null; steps: RunStep[];
}
export interface CostRow { key: string; label: string; runs: number; succeededRuns: number; inputTokens: number; outputTokens: number; costUsd: number; averageCostPerRunUsd: number; averageDurationMinutes: number }
export interface CostReport { from: string; to: string; groupBy: string; totalCostUsd: number; totalTokens: number; totalRuns: number; costPerSucceededRunUsd: number; rows: CostRow[] }
export interface BudgetLine { scope: string; name: string; limitUsd: number | null; spentUsd: number; usedPercent: number | null }

export interface TeamRoleMapping { team: string; role: ProjectRole }
export interface GroupRoleMapping { groupId: string; role: ProjectRole }
export interface OrganizationSettings {
  autoDispatch: boolean; requireRunApproval: boolean; updateWorkItemState: boolean; autoReviewPullRequests: boolean; syncIntervalMinutes: number;
  autoProvisionMembers: boolean; autoInviteTeamMembers: boolean; allowSelfSignUp: boolean; createDefaultAgentForDevelopers: boolean; requireUserCredentialForWrites: boolean;
  teamRoles: TeamRoleMapping[]; groupRoles: GroupRoleMapping[]; defaultRuntime: AgentRuntime; defaultProvider: ModelProvider; defaultModel: string; azureOpenAiEndpoint: string | null;
}
export interface AzureConnection {
  organizationUrl: string | null; organizationName: string; credentialType: "pat" | "service-principal" | "none"; patConfigured: boolean; servicePrincipalConfigured: boolean;
  lastSyncedAt: string | null; lastSyncError: string | null; projects: number; repositories: number; mirroredRepositories: number; teams: number; workItems: number;
  serviceHookPasswordConfigured: boolean;
}
export interface AiCredential { name: string; label: string; configured: boolean; updatedAt: string | null }
export interface AiProvider {
  key: string; name: string; kind: "api" | "cli"; runtime: AgentRuntime; provider: ModelProvider; defaultModel: string; description: string; credentials: AiCredential[];
  configured: boolean; installed: boolean | null; version: string | null; installHint: string | null;
}
export interface Settings { azure: AzureConnection; automation: OrganizationSettings; monthlyBudgetUsd: number | null; providers: AiProvider[]; entraEnabled: boolean; emailEnabled: boolean }
export interface ProviderStatus { key: string; name: string; kind: "api" | "cli"; runtime: AgentRuntime; provider: ModelProvider; defaultModel: string; description: string; configured: boolean; installed: boolean | null; version: string | null }
export interface ProviderStatusList { providers: ProviderStatus[]; defaultRuntime: AgentRuntime; defaultProvider: ModelProvider; defaultModel: string }
export interface UserProjectRole { projectId: string; projectName: string; role: ProjectRole; source: MembershipSource }
export interface OrganizationUser {
  userId: string; email: string; displayName: string; role: OrganizationRole; isActive: boolean; lastLoginAt: string | null; azureConnected: boolean;
  signsInWithMicrosoft: boolean; agents: number; projects: UserProjectRole[];
}
export interface AzurePerson { uniqueName: string; displayName: string; projects: string[]; invitationStatus: InvitationStatus | null }
export interface Invitation { id: string; email: string; displayName: string; role: OrganizationRole; status: InvitationStatus; expiresAt: string; createdAt: string; invitedBy: string; projectId: string | null }
export interface InvitationLink { id: string; email: string; url: string; emailSent: boolean }
export interface Users { users: OrganizationUser[]; withoutAccount: AzurePerson[]; invitations: Invitation[] }
export interface AzureCredential { id: string; kind: "PersonalAccessToken" | "OAuth"; azureOrganizationUrl: string | null; azureUniqueName: string; azureDisplayName: string | null; verifiedAt: string; expiresAt: string | null; lastError: string | null }
export interface AuditEntry { at: string; actor: string; action: string; entityType: string; entityId: string; details: string | null; projectId: string | null }

// Formatting ------------------------------------------------------------------------------------

export const fmtDate = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString("tr-TR", { dateStyle: "short", timeStyle: "short" }) : "—";
export const fmtDay = (value: string | null | undefined) => (value ? new Date(value).toLocaleDateString("tr-TR", { day: "numeric", month: "short", year: "numeric" }) : "—");
export const fmtAgo = (value: string | null | undefined) => {
  if (!value) return "—";
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60_000);
  if (minutes < 1) return "az önce";
  if (minutes < 60) return `${minutes} dk önce`;
  if (minutes < 60 * 24) return `${Math.round(minutes / 60)} sa önce`;
  if (minutes < 60 * 24 * 30) return `${Math.round(minutes / 1440)} gün önce`;
  return fmtDay(value);
};
export const fmtUsd = (value: number | null | undefined, digits = 2) =>
  value === null || value === undefined ? "—" : `$${value.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: Math.max(digits, 4) })}`;
export const fmtTokens = (value: number) => value >= 1_000_000 ? `${(value / 1_000_000).toFixed(2)}M` : value >= 1_000 ? `${(value / 1_000).toFixed(1)}k` : `${value}`;
export const fmtBytes = (value: number) => value >= 1_048_576 ? `${(value / 1_048_576).toFixed(1)} MB` : value >= 1024 ? `${(value / 1024).toFixed(1)} KB` : `${value} B`;

export const RUN_STATUS: Record<RunStatus, { label: string; cls: string }> = {
  PendingApproval: { label: "Onay bekliyor", cls: "warn" },
  Queued: { label: "Sırada", cls: "warn" },
  Running: { label: "Çalışıyor", cls: "info" },
  Succeeded: { label: "Tamamlandı", cls: "ok" },
  Failed: { label: "Başarısız", cls: "bad" },
  Cancelled: { label: "İptal", cls: "" },
};
export const ROLE_LABEL: Record<ProjectRole, string> = { Viewer: "İzleyici", Developer: "Developer", Reviewer: "Reviewer", Maintainer: "Maintainer", Admin: "Proje yöneticisi" };
export const ORG_ROLE_LABEL: Record<OrganizationRole, string> = { Member: "Üye", Admin: "Yönetici", Owner: "Sahip" };
export const SOURCE_LABEL: Record<MembershipSource, string> = { Manual: "Elle", AzureTeam: "Azure takımı", EntraGroup: "Entra grubu" };
export const INVITATION_LABEL: Record<InvitationStatus, { label: string; cls: string }> = {
  Pending: { label: "Davet bekliyor", cls: "warn" }, Accepted: { label: "Kabul edildi", cls: "ok" }, Expired: { label: "Süresi doldu", cls: "bad" }, Revoked: { label: "İptal", cls: "" },
};
export const RUNTIME_LABEL: Record<AgentRuntime, string> = { Framework: "Agent Framework", ClaudeCode: "Claude Code CLI", CursorCli: "Cursor CLI" };
export const PROVIDER_LABEL: Record<ModelProvider, string> = { Anthropic: "Anthropic", OpenAI: "OpenAI", AzureOpenAI: "Azure OpenAI", Gemini: "Gemini" };
export const VERDICT_LABEL: Record<ReviewVerdict, { label: string; cls: string }> = {
  Approve: { label: "Onaylandı", cls: "ok" }, Comment: { label: "Yorum", cls: "info" }, RequestChanges: { label: "Değişiklik istendi", cls: "bad" },
};
