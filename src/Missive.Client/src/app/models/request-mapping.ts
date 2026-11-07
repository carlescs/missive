import { RequestDto, WorkspaceNodeDto } from './messages';
import {
  AUTH_TYPES,
  BODY_TYPES,
  HTTP_METHODS,
  HttpMethod,
  RequestAuth,
  RequestContent,
  WorkspaceNode,
} from './workspace';

export const EMPTY_AUTH: RequestAuth = {
  type: 'none',
  username: '',
  password: '',
  token: '',
  key: '',
  value: '',
  in: 'header',
};

function toMethod(value: string | null | undefined): HttpMethod {
  const upper = (value ?? '').toUpperCase();
  return (HTTP_METHODS as readonly string[]).includes(upper) ? (upper as HttpMethod) : 'GET';
}

export function fromRequestDto(dto: RequestDto): RequestContent {
  return {
    name: dto.name,
    method: toMethod(dto.method),
    url: dto.url,
    params: dto.params.map((r) => ({ ...r })),
    headers: dto.headers.map((r) => ({ ...r })),
    auth: {
      type: AUTH_TYPES.includes(dto.auth.type) ? dto.auth.type : 'none',
      username: dto.auth.username ?? '',
      password: dto.auth.password ?? '',
      token: dto.auth.token ?? '',
      key: dto.auth.key ?? '',
      value: dto.auth.value ?? '',
      in: dto.auth.in === 'query' ? 'query' : 'header',
    },
    bodyType: BODY_TYPES.includes(dto.body.type) ? dto.body.type : 'none',
    body: dto.body.content,
    description: dto.description ?? '',
  };
}

/** Builds the DTO the host saves. Only the fields of the chosen auth type are sent. */
export function toRequestDto(content: RequestContent): RequestDto {
  const { auth } = content;
  return {
    name: content.name,
    method: content.method,
    url: content.url,
    params: content.params.map((r) => ({ ...r })),
    headers: content.headers.map((r) => ({ ...r })),
    auth:
      auth.type === 'basic'
        ? { type: 'basic', username: auth.username, password: auth.password }
        : auth.type === 'bearer'
          ? { type: 'bearer', token: auth.token }
          : auth.type === 'apiKey'
            ? { type: 'apiKey', key: auth.key, value: auth.value, in: auth.in }
            : { type: 'none' },
    body: { type: content.bodyType, content: content.bodyType === 'none' ? '' : content.body },
    description: content.description,
  };
}

export function fromNodeDto(node: WorkspaceNodeDto): WorkspaceNode {
  return {
    path: node.path,
    name: node.name,
    kind: node.kind,
    method: node.kind === 'request' && node.method ? toMethod(node.method) : undefined,
    error: node.error ?? undefined,
    children: node.children?.map(fromNodeDto),
  };
}
