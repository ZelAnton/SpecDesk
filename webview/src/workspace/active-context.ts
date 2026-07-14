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

const PUBLISHED: StatusPayload = { state: "published", label: "Published" };

function fileType(path: string): ActiveFileType {
  const lower = path.toLowerCase();
  return lower.endsWith(".md") || lower.endsWith(".markdown") ? "markdown" : "other";
}

function normalizePath(path: string): string {
  return path.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
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
  private status: StatusPayload = PUBLISHED;

  documentLoaded(path: string): ActiveContext {
    if (this.documentPath !== null) this.status = PUBLISHED;
    this.documentPath = path;
    return this.current();
  }

  documentCleared(): ActiveContext {
    this.documentPath = null;
    this.workspace = null;
    this.status = PUBLISHED;
    return this.current();
  }
  workspaceChanged(workspace: WorkspaceContextPayload): ActiveContext {
    this.workspace = workspace;
    return this.current();
  }

  statusChanged(status: StatusPayload): ActiveContext {
    this.status = status;
    return this.current();
  }

  current(): ActiveContext {
    if (this.documentPath === null) return EMPTY_ACTIVE_CONTEXT;

    const workspace =
      this.workspace !== null && workspaceMatchesDocument(this.workspace, this.documentPath)
        ? this.workspace
        : null;
    const repository: RepositoryContext | null =
      workspace?.repository === undefined || workspace.repository === null
        ? null
        : {
            kind: "repository",
            id: workspace.repository,
            root: workspace.repositoryRoot,
            defaultBranch: workspace.defaultBranch,
          };
    const branch: BranchContext | null =
      repository !== null && workspace?.branchState === "named" && workspace.branch !== null
        ? { kind: "branch", repository, name: workspace.branch }
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
