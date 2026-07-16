import {
  isReviewState,
  type StatusPayload,
  type WorkspaceContextPayload,
} from "../wire/protocol.js";

export type ActiveFileType = "markdown" | "other";

export interface RepositoryContext {
  readonly kind: "repository";
  readonly id: string;
  readonly root: string | null;
  readonly defaultBranch: string | null;
}

export interface BranchContext {
  readonly kind: "branch";
  readonly repository: RepositoryContext;
  readonly name: string;
}

export interface PullRequestContext {
  readonly kind: "pullRequest";
  /** A pull request structurally includes the named branch it reviews. */
  readonly branch: BranchContext;
}

export interface FileContext {
  readonly kind: "file";
  readonly path: string;
  readonly type: ActiveFileType;
  readonly repository: RepositoryContext | null;
}

/** Repository, branch, pull-request, and file capabilities compose without an enum cross-product. */
export interface ActiveContext {
  readonly repository: RepositoryContext | null;
  readonly branch: BranchContext | null;
  readonly pullRequest: PullRequestContext | null;
  readonly file: FileContext | null;
}

export const EMPTY_ACTIVE_CONTEXT: ActiveContext = {
  repository: null,
  branch: null,
  pullRequest: null,
  file: null,
};

export interface DocumentContextHint {
  readonly repository: string | null;
  readonly branch: string | null;
  readonly repositoryPath: string | null;
}

const PUBLISHED: StatusPayload = { state: "published", label: "Published" };

function fileType(path: string): ActiveFileType {
  const lower = path.toLowerCase();
  return lower.endsWith(".md") || lower.endsWith(".markdown") ? "markdown" : "other";
}

function normalizePath(path: string): string {
  return path.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
}

function hintedRepositoryRoot(documentPath: string, repositoryPath: string | null): string | null {
  if (repositoryPath === null || documentPath.startsWith("github://")) return null;
  const relative = repositoryPath.replaceAll("\\", "/").replace(/^\/+/, "");
  const document = documentPath.replaceAll("\\", "/");
  if (relative.length === 0 || !document.toLowerCase().endsWith(`/${relative.toLowerCase()}`)) {
    return null;
  }
  const root = document.slice(0, -(relative.length + 1));
  return documentPath.includes("\\") ? root.replaceAll("/", "\\") : root;
}

interface RemoteDocumentIdentity {
  readonly repository: string;
  readonly branch: string;
  readonly path: string;
}

function parseRemoteDocumentIdentity(documentPath: string): RemoteDocumentIdentity | null {
  const match = /^github:\/\/([^/]+)\/([^/]+)\/([^/]+)\/(.+)$/i.exec(documentPath);
  if (match === null) return null;

  const [, owner, name, encodedBranch, encodedPath] = match;
  if (
    owner === undefined ||
    name === undefined ||
    encodedBranch === undefined ||
    encodedPath === undefined
  ) {
    return null;
  }

  try {
    const branch = decodeURIComponent(encodedBranch);
    const path = decodeURIComponent(encodedPath);
    if (branch.length === 0 || path.length === 0) return null;
    return { repository: `${owner}/${name}`, branch, path };
  } catch {
    return null;
  }
}

function workspaceMatchesDocument(
  workspace: WorkspaceContextPayload,
  documentPath: string,
): boolean {
  const document = normalizePath(documentPath);
  const relative = normalizePath(workspace.path).replace(/^\/+/, "");
  if (workspace.repositoryRoot !== null) {
    return document === `${normalizePath(workspace.repositoryRoot)}/${relative}`;
  }

  if (workspace.repository !== null) {
    const remote = parseRemoteDocumentIdentity(documentPath);
    return (
      remote !== null &&
      remote.repository.toLowerCase() === workspace.repository.toLowerCase() &&
      workspace.branchState === "named" &&
      workspace.branch !== null &&
      remote.branch === workspace.branch &&
      remote.path === workspace.path
    );
  }
  return document === relative || document.endsWith(`/${relative}`);
}

/** Order-independent owner of the three authoritative inputs. An initial status/context may arrive before
 * the first document; a later document resets the previous lifecycle while mismatched repository context
 * is ignored until the matching `workspace.context` frame arrives. */
export class ActiveContextModel {
  private documentPath: string | null = null;
  private workspace: WorkspaceContextPayload | null = null;
  private workspaceReceivedAfterDocument = false;
  private documentHint: DocumentContextHint | null = null;
  private status: StatusPayload = PUBLISHED;
  private explicitPullRequest: ActiveContext | null = null;

  documentLoaded(path: string, hint: DocumentContextHint | null = null): ActiveContext {
    this.explicitPullRequest = null;
    if (this.documentPath !== null) this.status = PUBLISHED;
    this.documentPath = path;
    this.documentHint = hint;
    this.workspaceReceivedAfterDocument = false;
    return this.current();
  }

  documentCleared(): ActiveContext {
    this.explicitPullRequest = null;
    this.documentPath = null;
    this.documentHint = null;
    this.workspace = null;
    this.workspaceReceivedAfterDocument = false;
    this.status = PUBLISHED;
    return this.current();
  }
  workspaceChanged(workspace: WorkspaceContextPayload): ActiveContext {
    this.workspace = workspace;
    this.workspaceReceivedAfterDocument =
      this.documentPath !== null &&
      workspaceMatchesDocument(workspace, this.documentPath) &&
      (this.documentHint?.repository === null ||
        this.documentHint?.repository === undefined ||
        workspace.repository?.toLowerCase() === this.documentHint.repository.toLowerCase());
    return this.current();
  }

  statusChanged(status: StatusPayload): ActiveContext {
    this.status = status;
    return this.current();
  }

  pullRequestOpened(repositoryId: string, branchName: string): ActiveContext {
    const repository: RepositoryContext = {
      kind: "repository",
      id: repositoryId,
      root: null,
      defaultBranch: null,
    };
    const branch: BranchContext = { kind: "branch", repository, name: branchName };
    this.explicitPullRequest = {
      repository,
      branch,
      pullRequest: { kind: "pullRequest", branch },
      file: null,
    };
    return this.explicitPullRequest;
  }

  pullRequestLoading(): ActiveContext {
    this.explicitPullRequest = EMPTY_ACTIVE_CONTEXT;
    return this.explicitPullRequest;
  }

  pullRequestClosed(): ActiveContext {
    this.explicitPullRequest = null;
    return this.current();
  }

  current(): ActiveContext {
    if (this.explicitPullRequest !== null) return this.explicitPullRequest;
    if (this.documentPath === null) return EMPTY_ACTIVE_CONTEXT;

    const workspace =
      this.workspace !== null &&
      workspaceMatchesDocument(this.workspace, this.documentPath) &&
      (this.documentHint?.repository === null ||
        this.documentHint?.repository === undefined ||
        this.workspace.repository?.toLowerCase() === this.documentHint.repository.toLowerCase())
        ? this.workspace
        : null;
    const repositoryId = workspace?.repository ?? this.documentHint?.repository ?? null;
    const repository: RepositoryContext | null =
      repositoryId === null
        ? null
        : {
            kind: "repository",
            id: repositoryId,
            root:
              workspace?.repositoryRoot ??
              hintedRepositoryRoot(this.documentPath, this.documentHint?.repositoryPath ?? null),
            defaultBranch: workspace?.defaultBranch ?? null,
          };
    const branchName =
      workspace !== null && this.workspaceReceivedAfterDocument
        ? workspace.branchState === "named"
          ? workspace.branch
          : null
        : this.documentHint !== null
          ? this.documentHint.branch
          : workspace?.branchState === "named"
            ? workspace.branch
            : null;
    const branch: BranchContext | null =
      repository !== null && branchName !== null
        ? { kind: "branch", repository, name: branchName }
        : null;
    const file: FileContext = {
      kind: "file",
      path: this.documentPath,
      type: fileType(this.documentPath),
      repository,
    };
    return {
      repository,
      branch,
      pullRequest:
        branch !== null && isReviewState(this.status.state) && this.status.branch === branch.name
          ? { kind: "pullRequest", branch }
          : null,
      file,
    };
  }
}

export function rightToolsForContext(context: ActiveContext): ReadonlySet<string> {
  const ids = new Set<string>(["assistant"]);
  if (context.pullRequest !== null) ids.add("comments");
  if (context.branch !== null) ids.add("history");
  if (context.file !== null && context.file.repository !== null) ids.add("versions");
  return ids;
}
