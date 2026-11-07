import { CaptureDto, ResponseDto } from './messages';

export const HTTP_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] as const;
export type HttpMethod = (typeof HTTP_METHODS)[number];

export const BODY_TYPES = ['none', 'json', 'text', 'form', 'urlencoded'] as const;
export type BodyType = (typeof BODY_TYPES)[number];

export const AUTH_TYPES = ['none', 'basic', 'bearer', 'apiKey'] as const;
export type AuthType = (typeof AUTH_TYPES)[number];

export interface KeyValue {
  key: string;
  value: string;
  enabled: boolean;
}

export type ApiKeyLocation = 'header' | 'query';

/** Auth settings. Fields for other types are kept while editing so switching type loses nothing. */
export interface RequestAuth {
  type: AuthType;
  username: string;
  password: string;
  token: string;
  key: string;
  value: string;
  in: ApiKeyLocation;
}

/** The editable content of a request file. */
export interface RequestContent {
  name: string;
  method: HttpMethod;
  url: string;
  params: KeyValue[];
  headers: KeyValue[];
  auth: RequestAuth;
  bodyType: BodyType;
  body: string;
  /** Notes about the request, in Markdown. */
  description: string;
  /** Values to capture from a successful response into variables. */
  captures: CaptureDto[];
}

/** The outcome of the last send of a request: its response, or why there is none. */
export type ResponseState =
  | {
      kind: 'response';
      response: ResponseDto;
      /** The id the request was sent with; the host keeps the raw body under it for saving. */
      sendId: string;
    }
  | { kind: 'error'; code: string; message: string };

/** A request open in the editor. Mirrors one request YAML file in the workspace. */
export interface RequestDocument extends RequestContent {
  /** Workspace-relative file path; `null` for a new request that has not been saved yet. */
  path: string | null;
  /** Where a new request will be saved (`''` is the workspace root). */
  folder: string;
  /** Stable id for the open document (the path, or a generated id for unsaved requests). */
  id: string;
  /** The document's tab key (see `WorkspaceStore.tabs`); unlike `id`, never changes. */
  key: string;
  dirty: boolean;
  saving: boolean;
  /** The id of the send in progress, or `null` when the request isn't being sent. */
  sendId: string | null;
  /** The last response (or send error); kept in memory only. */
  response: ResponseState | null;
  /** Incremented on every edit, so a save can tell whether the document changed meanwhile. */
  revision: number;
}

export interface WorkspaceNode {
  /** Workspace-relative path. */
  path: string;
  name: string;
  kind: 'folder' | 'request';
  /** Only for requests: shown as a badge in the tree. */
  method?: HttpMethod;
  /** Only for requests whose file could not be read: why. */
  error?: string;
  children?: WorkspaceNode[];
}

export interface WorkspaceInfo {
  name: string;
  /** Absolute path of the workspace folder. */
  root: string;
}

export const METHOD_COLORS: Record<HttpMethod, string> = {
  GET: 'text-emerald-600',
  POST: 'text-amber-600',
  PUT: 'text-sky-600',
  PATCH: 'text-violet-600',
  DELETE: 'text-rose-600',
  HEAD: 'text-slate-500',
  OPTIONS: 'text-slate-500',
};

export const AUTH_LABELS: Record<AuthType, string> = {
  none: 'No auth',
  basic: 'Basic',
  bearer: 'Bearer token',
  apiKey: 'API key',
};

export const BODY_LABELS: Record<BodyType, string> = {
  none: 'None',
  json: 'JSON',
  text: 'Raw text',
  form: 'Form data',
  urlencoded: 'x-www-form-urlencoded',
};
