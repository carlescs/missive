/**
 * Messages exchanged with the .NET host over the Photino bridge. Keep in sync with
 * `src/Missive.Desktop/Bridge/BridgeMessages.cs`.
 */

export interface KeyValueDto {
  key: string;
  value: string;
  enabled: boolean;
}

export interface AuthDto {
  type: 'none' | 'basic' | 'bearer' | 'apiKey';
  username?: string | null;
  password?: string | null;
  token?: string | null;
  key?: string | null;
  value?: string | null;
  in?: 'header' | 'query' | null;
}

export interface BodyDto {
  type: 'none' | 'json' | 'text' | 'form' | 'urlencoded';
  content: string;
}

export interface RequestDto {
  name: string;
  method: string;
  url: string;
  params: KeyValueDto[];
  headers: KeyValueDto[];
  auth: AuthDto;
  body: BodyDto;
  /** Notes about the request, in Markdown; empty or missing when there are none. */
  description?: string | null;
}

export interface SavedRequestDto {
  path: string;
  request: RequestDto;
}

export interface ResponseDto {
  status: number;
  /** The reason phrase, e.g. `Not Found`; may be empty. */
  statusText: string;
  /** In the order received; names may repeat. */
  headers: { key: string; value: string }[];
  /** The media type without parameters, e.g. `application/json`. */
  contentType?: string | null;
  /** The body as text, or base64 when `bodyEncoding` is `base64` (binary content). */
  body: string;
  bodyEncoding: 'text' | 'base64';
  /** Size of the received body in bytes. */
  size: number;
  /** Whether the body was too large and was cut off. */
  truncated: boolean;
  durationMs: number;
  /** The file name suggested when saving the body (from `Content-Disposition`, or the URL and type). */
  fileName?: string;
  /** Where the time went, in milliseconds; absent when it wasn't measured. */
  timings?: TimingsDto | null;
  /** The redirects followed before this response, in order. */
  redirects?: RedirectDto[] | null;
  /** The request as it went on the wire (after redirects), with secrets hidden. */
  sent?: SentRequestDto | null;
  /** The cookies set by this response and by the redirects before it. */
  cookies?: ResponseCookieDto[] | null;
}

/**
 * The phases of an exchange in milliseconds, summed over redirect hops. Connection phases are
 * `null` when no new connection was opened.
 */
export interface TimingsDto {
  dns: number | null;
  connect: number | null;
  tls: number | null;
  /** From sending each request until its response headers arrived (time to first byte). */
  wait: number;
  /** Reading the final response's body. */
  download: number;
}

export interface RedirectDto {
  method: string;
  /** The URL that answered with the redirect. */
  url: string;
  status: number;
  statusText: string;
  /** Where it redirected to. */
  location: string;
  durationMs: number;
}

export interface SentRequestDto {
  method: string;
  url: string;
  /** In the order sent, including the ones the host adds (User-Agent, Content-Length, Cookie…). */
  headers: { key: string; value: string }[];
}

/** A cookie a response set, as written in its `Set-Cookie` header. */
export interface ResponseCookieDto {
  name: string;
  value: string;
  domain?: string | null;
  path?: string | null;
  /** ISO date; `null` for a session cookie. */
  expires?: string | null;
  secure: boolean;
  httpOnly: boolean;
  sameSite?: string | null;
}

/** A cookie kept in an environment's cookie jar. */
export interface CookieDto {
  name: string;
  value: string;
  domain: string;
  path: string;
  /** ISO date; `null` for a session cookie. */
  expires?: string | null;
  secure: boolean;
  httpOnly: boolean;
}

export interface EnvironmentVariableDto {
  name: string;
  value: string;
  /** Whether the value is kept on this computer only (in `.missive/`) instead of in the file. */
  secret: boolean;
}

export interface EnvironmentDto {
  name: string;
  variables: EnvironmentVariableDto[];
  /** Whether requests sent with it keep cookies between them (stored in `.missive/`). */
  cookieJar?: boolean;
}

export interface SavedEnvironmentDto {
  /** The environment's file name in the `environments` folder, e.g. `dev.yaml`. */
  id: string;
  environment: EnvironmentDto;
}

export interface EnvironmentSummaryDto {
  id: string;
  /** The environment's name, or its file name when the file can't be read. */
  name: string;
  /** Why the file can't be read, if it can't. */
  error?: string | null;
}

export interface EnvironmentListDto {
  /** The selected environment's id, or `null` when none is selected. */
  active: string | null;
  /** Sorted by name. */
  environments: EnvironmentSummaryDto[];
}

export interface FolderDto {
  path: string;
}

export interface WorkspaceNodeDto {
  path: string;
  name: string;
  kind: 'folder' | 'request';
  method?: string | null;
  error?: string | null;
  children?: WorkspaceNodeDto[] | null;
}

export interface WorkspaceDto {
  name: string;
  root: string;
  nodes: WorkspaceNodeDto[];
}

/** A format the workspace can be exported to. */
export interface ExportFormatDto {
  /** Passed to `workspace.export`, e.g. `postman`. */
  id: string;
  name: string;
  /** The main file's extension, with its dot. */
  extension: string;
}

export interface ExportResultDto {
  /** The main file written. */
  path: string;
  /** Every file written, the main one first (Postman environments are separate files). */
  files: string[];
  requests: number;
  environments: number;
  /** What was skipped or could not be represented. */
  warnings: string[];
}

export interface ImportResultDto {
  /** The format the file was read as, e.g. `Postman collection v2.1`. */
  format: string;
  /** The folder created for the imported requests; `null` when the file had none. */
  folder: string | null;
  requests: number;
  folders: number;
  /** Ids of the environments created. */
  environments: string[];
  /** What was skipped or imported only approximately. */
  warnings: string[];
}

export type GitFileStatus =
  'modified' | 'added' | 'deleted' | 'renamed' | 'untracked' | 'conflicted';

export interface GitChange {
  /** Path relative to the workspace folder, with `/` separators. */
  path: string;
  status: GitFileStatus;
  /** For a rename, the path the file was renamed from. */
  originalPath?: string | null;
}

export interface GitStatus {
  /** The checked-out branch; `null` when HEAD is detached. */
  branch?: string | null;
  /** The branch's upstream, e.g. `origin/main`; `null` when none is set. */
  upstream?: string | null;
  ahead: number;
  behind: number;
  /** Whether a merge is in progress. */
  merging: boolean;
  changes: GitChange[];
  /** During a merge, git's proposed commit message. */
  mergeMessage?: string | null;
  /** Whether the branch has no commits yet (a new repository). */
  unborn?: boolean;
  /** Whether the upstream branch was deleted on the remote (as of the last fetch). */
  upstreamGone?: boolean;
}

/** The name and email git records on commits; a part is `null` when it is not configured. */
export interface GitIdentity {
  name: string | null;
  email: string | null;
}

/** Both sides of a conflicted file; a side is `null` when it deleted the file. */
export interface GitConflict {
  path: string;
  /** The common ancestor; `null` when both sides added the file. */
  base: string | null;
  /** The current branch's version. */
  ours: string | null;
  /** The incoming version. */
  theirs: string | null;
  /** The file on disk, usually with conflict markers; `null` if it was deleted. */
  working: string | null;
  hasConflictMarkers: boolean;
}

/** A commit in the workspace's history. */
export interface GitCommitInfo {
  /** The full commit id. */
  id: string;
  /** Parent commit ids: none for the first commit, two or more for a merge. */
  parents: string[];
  authorName: string;
  authorEmail: string;
  /** When it was authored, as an ISO 8601 date-time. */
  date: string;
  /** The first line of the commit message. */
  subject: string;
}

export interface GitHistory {
  /** Newest first. */
  commits: GitCommitInfo[];
  /** Whether older commits follow this page. */
  hasMore: boolean;
}

export interface GitCommitDetails {
  commit: GitCommitInfo;
  /** The commit message after the subject line; empty when there is none. */
  body: string;
  /** Workspace files the commit changed, compared with its first parent. */
  files: GitChange[];
}

/** A file before and after a commit; a version is `null` when the file didn't exist there. */
export interface GitFileRevision {
  commitId: string;
  change: GitChange;
  before: string | null;
  after: string | null;
  /** Whether the file is binary; its versions are then left out. */
  binary: boolean;
  /** Whether the file is a request file, so it can also be compared as a request. */
  isRequest: boolean;
  /** The before and after versions parsed as requests, when they could be. */
  originalRequest?: RequestDto | null;
  modifiedRequest?: RequestDto | null;
  /** Why a version couldn't be read as a request. */
  requestError?: string | null;
}

/** How the user resolves a conflicted file: take one side, or keep their own edit. */
export type ConflictResolution = 'ours' | 'theirs' | 'edited';

export type PullOutcome = 'upToDate' | 'updated' | 'conflicted';

/**
 * A changed file as of the last commit (`original`) and in the working tree (`modified`). A text
 * is `null` when the file doesn't exist on that side, or when it is binary.
 */
export interface GitFileDiff {
  path: string;
  status: GitFileStatus;
  /** For a rename, the path the file had in the last commit. */
  originalPath?: string | null;
  binary: boolean;
  original: string | null;
  modified: string | null;
  /** Whether the file is a request file; its sides are then parsed into requests too. */
  isRequest: boolean;
  originalRequest?: RequestDto | null;
  modifiedRequest?: RequestDto | null;
  /** Why a side of a request file could not be read as a request, if it couldn't. */
  requestError?: string | null;
}

export interface GitRemote {
  name: string;
  fetchUrl: string;
  /** Equal to `fetchUrl` unless the remote has its own push URL. */
  pushUrl: string;
}

export interface UpdateStatus {
  /**
   * `unsupported`: this copy cannot update itself (not installed). `upToDate`: no newer version.
   * `ready`: a newer version is downloaded and installs on restart.
   */
  state: 'unsupported' | 'upToDate' | 'ready';
  currentVersion?: string | null;
  /** The downloaded version, when `state` is `ready`. */
  version?: string | null;
}

export type Theme = 'light' | 'dark';

/** The per-user settings the UI can change. */
export interface AppSettingsDto {
  theme: Theme;
}

/** A workspace folder opened recently. */
export interface RecentWorkspaceDto {
  /** The folder's name. */
  name: string;
  /** The folder's full path, passed to `workspace.open`. */
  path: string;
}

/** Every message type with its payload and result. */
export interface BridgeMessages {
  /** Shows a folder picker and opens the chosen folder; `null` when cancelled. */
  /** Reads the per-user settings (kept in the host's settings file). */
  'settings.get': { payload: void; result: AppSettingsDto };
  /** Remembers the color theme. */
  'settings.setTheme': { payload: { theme: Theme }; result: AppSettingsDto };
  'workspace.pick': { payload: void; result: WorkspaceDto | null };
  'workspace.open': { payload: { path: string }; result: WorkspaceDto };
  /** Reopens the workspace from the previous session; `null` when there is none. */
  'workspace.openLast': { payload: void; result: WorkspaceDto | null };
  /**
   * The workspace folders opened recently, the most recent first. Folders that no longer exist are
   * dropped from the list.
   */
  'workspace.recent': { payload: void; result: RecentWorkspaceDto[] };
  /** Re-reads the open workspace from disk. */
  'workspace.tree': { payload: void; result: WorkspaceDto };
  /** The formats `workspace.export` can write. */
  'workspace.exportFormats': { payload: void; result: ExportFormatDto[] };
  /**
   * Asks where to save, then exports the workspace's requests (and environments, without secret
   * values) for other apps; `null` when cancelled.
   */
  'workspace.export': {
    payload: { format: string; includeEnvironments: boolean };
    result: ExportResultDto | null;
  };
  /**
   * Asks for a file exported by another app (Postman, Insomnia, HAR, OpenAPI) and imports it into
   * a new folder inside `folder` (`''` is the root); `null` when cancelled.
   */
  'workspace.import': { payload: { folder: string }; result: ImportResultDto | null };
  'request.load': { payload: { path: string }; result: RequestDto };
  /** Saves a new request in a folder (`''` is the workspace root). */
  'request.create': { payload: { folder: string; request: RequestDto }; result: SavedRequestDto };
  /** Saves an existing request file; the result path differs if the file was renamed. */
  'request.save': { payload: { path: string; request: RequestDto }; result: SavedRequestDto };
  /** Copies a request next to it as "… copy", in a new file named after the copy. */
  'request.duplicate': { payload: { path: string }; result: SavedRequestDto };
  /** Moves a request file into `folder` (`''` is the root), keeping its file name. */
  'request.move': { payload: { path: string; folder: string }; result: { path: string } };
  /** Deletes a request file. */
  'request.delete': { payload: { path: string }; result: null };
  /**
   * Sends a request (saved or not) from the host and waits for the response. `id` is chosen by
   * the UI and can be passed to `request.cancel`.
   */
  'request.send': { payload: { id: string; request: RequestDto }; result: ResponseDto };
  /** Cancels a request being sent; the result says whether it was still being sent. */
  'request.cancel': { payload: { id: string }; result: boolean };
  /**
   * Asks where to save, then writes the body of the response to the send `id` exactly as it was
   * received; `null` when cancelled.
   */
  'response.saveBody': { payload: { id: string }; result: { path: string } | null };
  /** The cookies kept for an environment (`environment` is its id), without expired ones. */
  'cookie.list': { payload: { environment: string }; result: CookieDto[] };
  /** Deletes one cookie from an environment's jar; the result is what is left. */
  'cookie.delete': {
    payload: { environment: string; name: string; domain: string; path: string };
    result: CookieDto[];
  };
  /** Deletes every cookie kept for an environment. */
  'cookie.clear': { payload: { environment: string }; result: null };
  /** Creates a folder inside `parent` (`''` is the workspace root). */
  'folder.create': { payload: { parent: string; name: string }; result: FolderDto };
  /** Renames a folder within its parent; the result is its new path. */
  'folder.rename': { payload: { path: string; name: string }; result: FolderDto };
  /** Moves a folder into `parent` (`''` is the root), keeping its name. */
  'folder.move': { payload: { path: string; parent: string }; result: FolderDto };
  /** Copies a folder with everything in it next to it as "… copy" ("… copy 2", … if taken). */
  'folder.duplicate': { payload: { path: string }; result: FolderDto };
  /** Deletes a folder with its requests; refused if it holds other files. */
  'folder.delete': { payload: { path: string }; result: null };
  /** The workspace's environments and the selected one. */
  'environment.list': { payload: void; result: EnvironmentListDto };
  /** Reads an environment, with the values of its secrets from this computer. */
  'environment.load': { payload: { id: string }; result: EnvironmentDto };
  /** Saves a new environment as a file named after it. */
  'environment.create': { payload: { environment: EnvironmentDto }; result: SavedEnvironmentDto };
  /** Saves an environment; the result id differs if the file was renamed. */
  'environment.save': {
    payload: { id: string; environment: EnvironmentDto };
    result: SavedEnvironmentDto;
  };
  /** Deletes an environment file and its stored secrets. */
  'environment.delete': { payload: { id: string }; result: null };
  /** Selects the environment requests are sent with; `null` selects none. */
  'environment.select': { payload: { id: string | null }; result: null };
  /** The open workspace's git status. */
  'git.status': { payload: void; result: GitStatus };
  /** Creates a git repository in the open workspace folder (`git init`). */
  'git.init': { payload: void; result: GitStatus };
  /** Commits `paths`, or every change in the workspace when `paths` is omitted. */
  'git.commit': {
    payload: { message: string; paths?: string[] };
    result: { commitId: string; status: GitStatus };
  };
  /** Discards the uncommitted changes of `paths`: edits are reverted and new files deleted. */
  'git.discard': { payload: { paths: string[] }; result: GitStatus };
  /** Pulls the current branch with a merge (never a rebase). */
  'git.pull': { payload: void; result: { outcome: PullOutcome; status: GitStatus } };
  /** Pushes the current branch, or publishes it when it has no upstream. Never forces. */
  'git.push': { payload: void; result: GitStatus };
  /** Downloads the remotes' latest commits without changing files, so `behind` is current. */
  'git.fetch': { payload: void; result: GitStatus };
  /** The commit identity git uses in the workspace. */
  'git.identity': { payload: void; result: GitIdentity };
  /** Sets the commit identity, for all of the user's repositories (`global`) or only this one. */
  'git.identity.set': {
    payload: { name: string; email: string; global: boolean };
    result: GitIdentity;
  };
  'git.conflict': { payload: { path: string }; result: GitConflict };
  /** Resolves one conflicted file; `edited` is refused while it still has conflict markers. */
  'git.resolve': { payload: { path: string; resolution: ConflictResolution }; result: GitStatus };
  /** Abandons the merge in progress (`git merge --abort`). */
  'git.abortMerge': { payload: void; result: GitStatus };
  /** A page of the commits that changed the workspace, newest first. */
  'git.log': { payload: { skip: number; limit?: number }; result: GitHistory };
  /** A commit with its full message and the workspace files it changed. */
  'git.show': { payload: { commitId: string }; result: GitCommitDetails };
  /** A file the commit changed, before and after it. */
  'git.commitFile': { payload: { commitId: string; path: string }; result: GitFileRevision };
  /** The committed and working tree versions of a changed file. */
  'git.diff': { payload: { path: string }; result: GitFileDiff };
  /** The repository's remotes, sorted by name. */
  'git.remote.list': { payload: void; result: GitRemote[] };
  /** Adds a remote; nothing is fetched or pushed. */
  'git.remote.add': { payload: { name: string; url: string }; result: GitRemote[] };
  /** Renames a remote and/or changes its URL (passing the current values keeps them). */
  'git.remote.update': {
    payload: { name: string; newName: string; url: string };
    result: GitRemote[];
  };
  /** Removes a remote and its remote-tracking branches. */
  'git.remote.remove': { payload: { name: string }; result: GitRemote[] };
  /** Looks for a newer version of the app and downloads it, without installing it. */
  'update.check': { payload: void; result: UpdateStatus };
  /** Installs the downloaded update and restarts the app; on success no response arrives. */
  'update.restart': { payload: void; result: null };
}

export type BridgeMessageType = keyof BridgeMessages;

export interface BridgeRequestEnvelope {
  id: string;
  type: BridgeMessageType;
  payload?: unknown;
}

export interface BridgeErrorShape {
  code: string;
  message: string;
}

export type BridgeResponseEnvelope =
  { id: string; ok: true; payload: unknown } | { id: string; ok: false; error: BridgeErrorShape };

/** Error codes the host can return. */
export const ERROR_CODES = {
  noWorkspace: 'no_workspace',
  notFound: 'not_found',
  invalidPath: 'invalid_path',
  invalidFile: 'invalid_file',
  invalidRequest: 'invalid_request',
  alreadyExists: 'already_exists',
  folderNotEmpty: 'folder_not_empty',
  io: 'io_error',
  updateFailed: 'update_failed',
  networkError: 'http.network_error',
  timeout: 'http.timeout',
  cancelled: 'http.cancelled',
  alreadySending: 'http.already_sending',
  unknownMessage: 'unknown_message',
  badPayload: 'bad_payload',
  internal: 'internal_error',
  noHost: 'no_host',
} as const;
