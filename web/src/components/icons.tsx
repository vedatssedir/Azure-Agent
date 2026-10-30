// Lucide icons (the set shadcn/ui uses), looked up by the names the pages already pass.
import {
  Bot, ChevronRight, CircleDollarSign, Box, ExternalLink, File, Files, Folder, FolderGit2, GitBranch, GitCommitHorizontal,
  GitPullRequest, History, House, IterationCw, ListTodo, Play, Plus, RefreshCw, Search, Settings, SquareKanban, UserCheck, X,
  type LucideIcon,
} from "lucide-react";

const icons: Record<string, LucideIcon> = {
  home: House,
  mine: UserCheck,
  boards: SquareKanban,
  workitems: ListTodo,
  sprint: IterationCw,
  repos: FolderGit2,
  files: Files,
  commits: GitCommitHorizontal,
  branch: GitBranch,
  pr: GitPullRequest,
  agents: Bot,
  runs: Play,
  costs: CircleDollarSign,
  settings: Settings,
  folder: Folder,
  file: File,
  plus: Plus,
  close: X,
  external: ExternalLink,
  search: Search,
  refresh: RefreshCw,
  logo: Box,
  chevron: ChevronRight,
  history: History,
};

export function Icon({ name, size = 16 }: { name: string; size?: number }) {
  const Svg = icons[name] ?? File;
  return <Svg size={size} strokeWidth={1.8} aria-hidden />;
}
