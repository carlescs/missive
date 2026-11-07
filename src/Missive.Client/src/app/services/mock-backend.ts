import {
  Theme,
  BridgeRequestEnvelope,
  BridgeResponseEnvelope,
  CookieDto,
  ERROR_CODES,
  EnvironmentDto,
  RequestDto,
  ResponseDto,
  WorkspaceDto,
  WorkspaceNodeDto,
} from '../models/messages';
import { VARIABLE_PATTERN } from '../models/variables';

const sample = (
  name: string,
  method: string,
  url: string,
  extra: Partial<RequestDto> = {},
): RequestDto => ({
  name,
  method,
  url,
  params: [],
  headers: [{ key: 'Accept', value: 'application/json', enabled: true }],
  auth: { type: 'none' },
  body: { type: 'none', content: '' },
  ...extra,
});

/**
 * In-memory stand-in for the .NET host, used when the UI runs in a plain browser
 * (`npm start`). It mimics the host's behavior closely enough for UI work — slug file names,
 * renames on save — but nothing is written to disk.
 */
export class MockBackend {
  private readonly root = '/browser-preview/my-api';
  private readonly folders = new Set(['users', 'orders']);
  private readonly files = new Map<string, RequestDto>([
    ['users/get-user.yaml', sample('Get user', 'GET', '{{baseUrl}}/users/1')],
    [
      'users/create-user.yaml',
      sample('Create user', 'POST', '{{baseUrl}}/users', {
        headers: [{ key: 'Content-Type', value: 'application/json', enabled: true }],
        auth: { type: 'bearer', token: '{{apiToken}}' },
        body: { type: 'json', content: '{\n  "name": "Ada Lovelace"\n}\n' },
        description: 'Creates a user.\n\n- Needs a **bearer token** (`apiToken`).\n',
      }),
    ],
    ['orders/list-orders.yaml', sample('List orders', 'GET', '{{baseUrl}}/orders')],
    ['health.yaml', sample('Health check', 'GET', '{{baseUrl}}/health')],
  ]);
  private readonly environments = new Map<string, EnvironmentDto>([
    [
      'dev.yaml',
      {
        name: 'Development',
        cookieJar: true,
        variables: [
          { name: 'host', value: 'localhost:5000', secret: false },
          { name: 'baseUrl', value: 'http://{{host}}', secret: false },
          { name: 'apiToken', value: 'dev-token', secret: true },
        ],
      },
    ],
    [
      'prod.yaml',
      {
        name: 'Production',
        variables: [
          { name: 'baseUrl', value: 'https://api.example.com', secret: false },
          { name: 'apiToken', value: '', secret: true },
        ],
      },
    ],
  ]);
  private activeEnvironment: string | null = 'dev.yaml';
  private open = false;
  private theme: Theme = 'light';
  /** Sends in progress, by id: calling the function cancels it. */
  private readonly sends = new Map<string, () => void>();
  /** The file names suggested for recent responses, by send id, for `response.saveBody`. */
  private readonly bodies = new Map<string, string>();
  /** Cookie jars by environment id. */
  private readonly jars = new Map<string, CookieDto[]>();

  async handle(request: BridgeRequestEnvelope): Promise<BridgeResponseEnvelope> {
    const ok = (payload: unknown): BridgeResponseEnvelope => ({
      id: request.id,
      ok: true,
      payload,
    });
    const fail = (code: string, message: string): BridgeResponseEnvelope => ({
      id: request.id,
      ok: false,
      error: { code, message },
    });
    const payload = request.payload as Record<string, unknown> | undefined;

    // Sending works without a workspace, like in the host.
    if (request.type === 'request.send') {
      return this.send(request.id, payload?.['id'] as string, payload?.['request'] as RequestDto);
    }
    if (request.type === 'response.saveBody') {
      // No save dialog in a browser: pretend the user picked a file in their downloads folder.
      const name = this.bodies.get(payload?.['id'] as string);
      return name
        ? ok({ path: `/browser-preview/Downloads/${name}` })
        : fail(ERROR_CODES.notFound, 'The response is no longer available; send it again.');
    }
    if (request.type === 'request.cancel') {
      const cancel = this.sends.get(payload?.['id'] as string);
      cancel?.();
      return ok(!!cancel);
    }
    if (request.type === 'workspace.openLast') return ok(null);
    if (request.type === 'workspace.recent') {
      return ok([{ name: 'my-api (browser preview)', path: this.root }]);
    }
    if (request.type === 'settings.get') return ok({ theme: this.theme });
    if (request.type === 'settings.setTheme') {
      this.theme = payload?.['theme'] as Theme;
      return ok({ theme: this.theme });
    }
    // The browser preview is never installed, so it cannot update itself.
    if (request.type === 'update.check') return ok({ state: 'unsupported' });
    if (request.type === 'update.restart') {
      return fail(ERROR_CODES.updateFailed, 'The browser preview cannot update itself.');
    }
    if (request.type === 'workspace.pick' || request.type === 'workspace.open') {
      this.open = true;
      return ok(this.workspace());
    }
    if (!this.open) return fail(ERROR_CODES.noWorkspace, 'Open a workspace folder first.');

    switch (request.type) {
      case 'workspace.tree':
        return ok(this.workspace());
      case 'workspace.exportFormats':
        return ok([
          { id: 'postman', name: 'Postman collection v2.1', extension: '.postman_collection.json' },
          { id: 'insomnia', name: 'Insomnia v4', extension: '.insomnia.json' },
        ]);
      case 'workspace.export': {
        // No save dialog in a browser: pretend the user picked a file next to the workspace.
        const insomnia = payload?.['format'] === 'insomnia';
        const path = `${this.root}${insomnia ? '.insomnia.json' : '.postman_collection.json'}`;
        const envFiles =
          payload?.['includeEnvironments'] && !insomnia
            ? [...this.environments.keys()].map((id) =>
                id.replace(/\.yaml$/, '.postman_environment.json'),
              )
            : [];
        return ok({
          path,
          files: [path, ...envFiles],
          requests: this.files.size,
          environments: payload?.['includeEnvironments'] ? this.environments.size : 0,
          warnings: ['Browser preview: nothing was written to disk.'],
        });
      }
      case 'workspace.import': {
        // No open dialog in a browser: import a small sample collection.
        const parent = (payload?.['folder'] as string) ?? '';
        let name = 'Imported sample';
        for (let n = 2; this.exists(parent ? `${parent}/${name}` : name); n++) {
          name = `Imported sample ${n}`;
        }
        const folder = parent ? `${parent}/${name}` : name;
        this.folders.add(folder);
        this.files.set(
          `${folder}/list-products.yaml`,
          sample('List products', 'GET', '{{baseUrl}}/products', {
            params: [{ key: 'limit', value: '20', enabled: false }],
          }),
        );
        return ok({
          format: 'Postman collection v2.1',
          folder,
          requests: 1,
          folders: 1,
          environments: [],
          warnings: ['Browser preview: a sample collection was imported instead of a file.'],
        });
      }
      case 'environment.list':
        return ok({
          active: this.activeEnvironment,
          environments: [...this.environments]
            .map(([id, env]) => ({ id, name: env.name }))
            .sort((a, b) => a.name.localeCompare(b.name)),
        });
      case 'environment.load': {
        const env = this.environments.get(payload?.['id'] as string);
        return env ? ok(structuredClone(env)) : fail(ERROR_CODES.notFound, 'Not found.');
      }
      case 'environment.create':
      case 'environment.save': {
        const env = structuredClone(payload?.['environment'] as EnvironmentDto);
        env.name = env.name.trim();
        env.variables = env.variables
          .filter((v) => v.name.trim() || v.value)
          .map((v) => ({ ...v, name: v.name.trim() }))
          .sort((a, b) => Number(a.secret) - Number(b.secret));
        if (!env.name) return fail(ERROR_CODES.invalidRequest, 'The environment needs a name.');
        const bad = env.variables.find((v) => !v.name || /[{}\s]/.test(v.name));
        if (bad) return fail(ERROR_CODES.invalidRequest, `'${bad.name}' is not a valid name.`);
        let id = payload?.['id'] as string | undefined;
        if (!id) {
          id = `${slug(env.name)}.yaml`;
          for (let n = 2; this.environments.has(id); n++) id = `${slug(env.name)}-${n}.yaml`;
        }
        env.cookieJar = !!env.cookieJar;
        if (!env.cookieJar) this.jars.delete(id);
        this.environments.set(id, env);
        return ok({ id, environment: structuredClone(env) });
      }
      case 'cookie.list':
        return ok(structuredClone(this.jars.get(payload?.['environment'] as string) ?? []));
      case 'cookie.delete': {
        const id = payload?.['environment'] as string;
        const left = (this.jars.get(id) ?? []).filter(
          (c) =>
            c.name !== payload?.['name'] ||
            c.domain !== payload?.['domain'] ||
            c.path !== payload?.['path'],
        );
        this.jars.set(id, left);
        return ok(structuredClone(left));
      }
      case 'cookie.clear':
        this.jars.delete(payload?.['environment'] as string);
        return ok(null);
      case 'environment.delete': {
        const id = payload?.['id'] as string;
        this.environments.delete(id);
        this.jars.delete(id);
        if (this.activeEnvironment === id) this.activeEnvironment = null;
        return ok(null);
      }
      case 'environment.select':
        this.activeEnvironment = (payload?.['id'] as string | null) ?? null;
        return ok(null);
      case 'request.load': {
        const file = this.files.get(payload?.['path'] as string);
        return file ? ok(structuredClone(file)) : fail(ERROR_CODES.notFound, 'File not found.');
      }
      case 'request.create': {
        const folder = payload?.['folder'] as string;
        const dto = this.clean(payload?.['request'] as RequestDto);
        if (!dto.name) return fail(ERROR_CODES.invalidRequest, 'The request needs a name.');
        const path = this.freePath(folder, dto.name);
        this.files.set(path, dto);
        return ok({ path, request: structuredClone(dto) });
      }
      case 'request.save': {
        let path = payload?.['path'] as string;
        const dto = this.clean(payload?.['request'] as RequestDto);
        if (!dto.name) return fail(ERROR_CODES.invalidRequest, 'The request needs a name.');
        const previous = this.files.get(path);
        const folder = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
        const stem = path.slice(path.lastIndexOf('/') + 1).replace(/\.ya?ml$/, '');
        this.files.set(path, dto);
        if (previous && previous.name !== dto.name && stem === slug(previous.name)) {
          const target = this.freePath(folder, dto.name, path);
          this.files.delete(path);
          this.files.set(target, dto);
          path = target;
        }
        return ok({ path, request: structuredClone(dto) });
      }
      case 'folder.create': {
        const parent = payload?.['parent'] as string;
        const name = ((payload?.['name'] as string) ?? '').trim();
        if (!name || /[\\/<>:"|?*]/.test(name) || name.startsWith('.')) {
          return fail(ERROR_CODES.invalidPath, `'${name}' is not a valid folder name.`);
        }
        const path = parent ? `${parent}/${name}` : name;
        if (this.exists(path)) return fail(ERROR_CODES.alreadyExists, `'${path}' already exists.`);
        this.folders.add(path);
        return ok({ path });
      }
      case 'folder.rename': {
        const path = payload?.['path'] as string;
        const name = ((payload?.['name'] as string) ?? '').trim();
        if (!name || /[\\/<>:"|?*]/.test(name) || name.startsWith('.')) {
          return fail(ERROR_CODES.invalidPath, `'${name}' is not a valid folder name.`);
        }
        const target = join(parentOf(path), name);
        if (target === path) return ok({ path });
        if (this.exists(target))
          return fail(ERROR_CODES.alreadyExists, `'${target}' already exists.`);
        this.relocateFolder(path, target);
        return ok({ path: target });
      }
      case 'folder.move': {
        const path = payload?.['path'] as string;
        const parent = payload?.['parent'] as string;
        if (parent === parentOf(path)) return ok({ path });
        if (isWithin(parent, path)) {
          return fail(ERROR_CODES.invalidPath, `'${path}' can't be moved into itself.`);
        }
        const target = join(parent, nameOf(path));
        if (this.exists(target))
          return fail(ERROR_CODES.alreadyExists, `'${target}' already exists.`);
        this.relocateFolder(path, target);
        return ok({ path: target });
      }
      case 'request.move': {
        const path = payload?.['path'] as string;
        const folder = payload?.['folder'] as string;
        const dto = this.files.get(path);
        if (!dto) return fail(ERROR_CODES.notFound, `The request file '${path}' does not exist.`);
        const target = join(folder, nameOf(path));
        if (target === path) return ok({ path });
        if (this.exists(target))
          return fail(ERROR_CODES.alreadyExists, `'${target}' already exists.`);
        this.files.delete(path);
        this.files.set(target, dto);
        return ok({ path: target });
      }
      case 'request.duplicate': {
        const path = payload?.['path'] as string;
        const dto = this.files.get(path);
        if (!dto) return fail(ERROR_CODES.notFound, `The request file '${path}' does not exist.`);
        const copy = { ...structuredClone(dto), name: `${dto.name} copy` };
        const target = this.freePath(parentOf(path), copy.name);
        this.files.set(target, copy);
        return ok({ path: target, request: structuredClone(copy) });
      }
      case 'folder.duplicate': {
        const path = payload?.['path'] as string;
        if (!this.folders.has(path))
          return fail(ERROR_CODES.notFound, `The folder '${path}' does not exist.`);
        let target = `${path} copy`;
        for (let n = 2; this.exists(target); n++) target = `${path} copy ${n}`;
        const copyPath = (p: string) => target + p.slice(path.length);
        for (const folder of [...this.folders].filter((f) => isWithin(f, path)))
          this.folders.add(copyPath(folder));
        for (const [file, dto] of [...this.files].filter(([f]) => isWithin(f, path)))
          this.files.set(copyPath(file), structuredClone(dto));
        return ok({ path: target });
      }
      case 'request.delete': {
        const path = payload?.['path'] as string;
        if (!this.files.delete(path))
          return fail(ERROR_CODES.notFound, `The request file '${path}' does not exist.`);
        return ok(null);
      }
      case 'folder.delete': {
        const path = payload?.['path'] as string;
        for (const folder of [...this.folders])
          if (isWithin(folder, path)) this.folders.delete(folder);
        for (const file of [...this.files.keys()])
          if (isWithin(file, path)) this.files.delete(file);
        return ok(null);
      }
      default:
        return fail(ERROR_CODES.unknownMessage, `Unknown message type '${request.type}'.`);
    }
  }

  /**
   * Pretends to send a request: after a short delay it answers with an echo of what would be
   * sent. Like the host, it resolves variables from the selected environment and refuses
   * requests with undefined ones.
   */
  private async send(
    envelopeId: string,
    id: string,
    dto: RequestDto,
  ): Promise<BridgeResponseEnvelope> {
    const fail = (code: string, message: string): BridgeResponseEnvelope => ({
      id: envelopeId,
      ok: false,
      error: { code, message },
    });

    const variables = this.variables();
    const resolve = (text: string | null | undefined) =>
      (text ?? '').replace(VARIABLE_PATTERN, (match, name: string) => variables.get(name) ?? match);
    dto = {
      ...dto,
      url: resolve(dto.url),
      params: dto.params.map((r) => ({ ...r, key: resolve(r.key), value: resolve(r.value) })),
      headers: dto.headers.map((r) => ({ ...r, key: resolve(r.key), value: resolve(r.value) })),
      auth: Object.fromEntries(
        Object.entries(dto.auth).map(([k, v]) => [k, k === 'type' ? v : resolve(v as string)]),
      ) as RequestDto['auth'],
      body: { ...dto.body, content: resolve(dto.body.content) },
    };

    const enabled = (rows: RequestDto['params']) => rows.filter((r) => r.enabled && r.key);
    const text = [
      dto.url,
      ...[...dto.params, ...dto.headers].flatMap((r) => (r.enabled ? [r.key, r.value] : [])),
      dto.auth.token ?? '',
      dto.auth.username ?? '',
      dto.auth.password ?? '',
      dto.auth.key ?? '',
      dto.auth.value ?? '',
      dto.body.content,
    ].join('\n');
    const missing = [...new Set([...text.matchAll(VARIABLE_PATTERN)].map((m) => m[1]))];
    if (missing.length) {
      return fail(
        ERROR_CODES.invalidRequest,
        `The request uses variables that aren't defined: ${missing.sort().join(', ')}. ` +
          (this.activeEnvironment ? 'Add them to the environment.' : 'No environment is selected.'),
      );
    }

    let url: URL;
    const raw = dto.url.trim();
    try {
      url = new URL(raw.includes('://') ? raw : `http://${raw}`);
      if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error();
    } catch {
      return fail(ERROR_CODES.invalidRequest, `'${raw}' is not a valid http or https URL.`);
    }
    for (const param of enabled(dto.params)) url.searchParams.append(param.key, param.value);

    const started = performance.now();
    const cancelled = await new Promise<boolean>((resolve) => {
      const timer = setTimeout(() => resolve(false), 300 + Math.random() * 500);
      this.sends.set(id, () => {
        clearTimeout(timer);
        resolve(true);
      });
    });
    this.sends.delete(id);
    if (cancelled) return fail(ERROR_CODES.cancelled, 'The request was cancelled.');

    const headers = enabled(dto.headers).map((h) => ({ key: h.key, value: h.value }));
    const env = this.activeEnvironment ? this.environments.get(this.activeEnvironment) : null;
    const jarId = env?.cookieJar ? this.activeEnvironment : null;
    const jar = jarId ? (this.jars.get(jarId) ?? []) : [];
    const sentHeaders = [
      ...headers.map((h) =>
        /^authorization$/i.test(h.key) ? { ...h, value: `${h.value.split(' ')[0]} ••••••` } : h,
      ),
      { key: 'User-Agent', value: 'Missive' },
      ...(jar.length
        ? [{ key: 'Cookie', value: jar.map((c) => `${c.name}=${c.value}`).join('; ') }]
        : []),
    ];
    const body = JSON.stringify({
      preview: 'Browser preview: nothing was sent. This is what the host would send.',
      method: dto.method,
      url: url.toString(),
      headers: Object.fromEntries(headers.map((h) => [h.key, h.value])),
      auth: dto.auth.type,
      body: dto.body.type === 'none' ? null : dto.body.content,
    });
    const setCookie = { key: 'Set-Cookie', value: 'preview-session=abc123; Path=/; HttpOnly' };
    if (jarId && !jar.some((c) => c.name === 'preview-session')) {
      this.jars.set(jarId, [
        ...jar,
        {
          name: 'preview-session',
          value: 'abc123',
          domain: url.hostname,
          path: '/',
          secure: false,
          httpOnly: true,
        },
      ]);
    }
    const durationMs = Math.round(performance.now() - started);
    const segment = url.pathname.split('/').filter(Boolean).pop() ?? 'response';
    const fileName = segment.includes('.') ? segment : `${segment}.json`;
    this.bodies.set(id, fileName);
    return {
      id: envelopeId,
      ok: true,
      payload: {
        status: 200,
        statusText: 'OK',
        headers: [
          { key: 'Content-Type', value: 'application/json; charset=utf-8' },
          { key: 'Content-Length', value: String(new TextEncoder().encode(body).length) },
          setCookie,
        ],
        contentType: 'application/json',
        body,
        bodyEncoding: 'text',
        size: new TextEncoder().encode(body).length,
        truncated: false,
        durationMs,
        fileName,
        timings: {
          dns: 2.4,
          connect: 6.1,
          tls: url.protocol === 'https:' ? 21.7 : null,
          wait: Math.max(durationMs - 40, 1),
          download: 3.2,
        },
        redirects: [],
        sent: { method: dto.method, url: url.toString(), headers: sentHeaders },
        cookies: [
          {
            name: 'preview-session',
            value: 'abc123',
            path: '/',
            secure: false,
            httpOnly: true,
          },
        ],
      } satisfies ResponseDto,
    };
  }

  /** The selected environment's values, with references between variables expanded. */
  private variables(): Map<string, string> {
    const env = this.activeEnvironment ? this.environments.get(this.activeEnvironment) : null;
    const raw = new Map(
      (env?.variables ?? []).filter((v) => v.value).map((v) => [v.name, v.value]),
    );
    const resolved = new Map<string, string>();
    const resolve = (name: string, seen: Set<string>): string | undefined => {
      if (resolved.has(name)) return resolved.get(name);
      const value = raw.get(name);
      if (value === undefined || seen.has(name)) return undefined;
      const result = value.replace(
        VARIABLE_PATTERN,
        (match, inner: string) => resolve(inner, new Set([...seen, name])) ?? match,
      );
      resolved.set(name, result);
      return result;
    };
    for (const name of raw.keys()) resolve(name, new Set());
    return resolved;
  }

  private relocateFolder(path: string, target: string): void {
    const move = (p: string) => target + p.slice(path.length);
    for (const folder of [...this.folders].filter((f) => isWithin(f, path))) {
      this.folders.delete(folder);
      this.folders.add(move(folder));
    }
    for (const [file, dto] of [...this.files].filter(([f]) => isWithin(f, path))) {
      this.files.delete(file);
      this.files.set(move(file), dto);
    }
  }

  private exists(path: string): boolean {
    const lower = path.toLowerCase();
    return [...this.folders, ...this.files.keys()].some((p) => p.toLowerCase() === lower);
  }

  private clean(dto: RequestDto): RequestDto {
    const rows = (list: RequestDto['params']) => list.filter((r) => r.key || r.value);
    return {
      ...structuredClone(dto),
      name: dto.name.trim(),
      params: rows(dto.params),
      headers: rows(dto.headers),
    };
  }

  private freePath(folder: string, name: string, current?: string): string {
    for (let attempt = 1; ; attempt++) {
      const file = `${slug(name)}${attempt > 1 ? `-${attempt}` : ''}.yaml`;
      const path = folder ? `${folder}/${file}` : file;
      if (path === current || !this.files.has(path)) return path;
    }
  }

  private workspace(): WorkspaceDto {
    const nodes = (folder: string): WorkspaceNodeDto[] => {
      const prefix = folder ? `${folder}/` : '';
      const direct = (p: string) => p.startsWith(prefix) && !p.slice(prefix.length).includes('/');
      const byPath = (a: { path: string }, b: { path: string }) => a.path.localeCompare(b.path);
      const folders = [...this.folders]
        .filter(direct)
        .map((path) => ({
          path,
          name: path.slice(prefix.length),
          kind: 'folder' as const,
          children: nodes(path),
        }))
        .sort(byPath);
      const files = [...this.files]
        .filter(([path]) => direct(path))
        .map(([path, dto]) => ({
          path,
          name: dto.name,
          kind: 'request' as const,
          method: dto.method,
        }))
        .sort(byPath);
      return [...folders, ...files];
    };
    return { name: 'my-api (browser preview)', root: this.root, nodes: nodes('') };
  }
}

function parentOf(path: string): string {
  return path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
}

function nameOf(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

function join(folder: string, name: string): string {
  return folder ? `${folder}/${name}` : name;
}

function isWithin(path: string, folder: string): boolean {
  return path === folder || path.startsWith(`${folder}/`);
}

function slug(name: string): string {
  const value = name
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return value || 'request';
}
