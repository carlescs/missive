import { KeyValue, RequestAuth, RequestContent } from '../../../models/workspace';
import { EMPTY_AUTH } from '../../../models/request-mapping';

/** A request with nothing in it: the missing side of an added or deleted request file. */
export const EMPTY_REQUEST: RequestContent = {
  name: '',
  method: 'GET',
  url: '',
  params: [],
  headers: [],
  auth: EMPTY_AUTH,
  bodyType: 'none',
  body: '',
  description: '',
  captures: [],
};

/** How one row of a params or headers table changed. */
export type RowChange =
  | { kind: 'same'; row: KeyValue }
  | { kind: 'added'; row: KeyValue }
  | { kind: 'removed'; row: KeyValue }
  | { kind: 'changed'; before: KeyValue; row: KeyValue };

/** A text split around the part that changed: `prefix` + `removed`/`added` + `suffix`. */
export interface TextChange {
  prefix: string;
  removed: string;
  added: string;
  suffix: string;
}

/** The common prefix and suffix of two texts, and what differs between them. */
export function textChange(before: string, after: string): TextChange {
  let start = 0;
  const max = Math.min(before.length, after.length);
  while (start < max && before[start] === after[start]) start++;
  let end = 0;
  while (end < max - start && before[before.length - 1 - end] === after[after.length - 1 - end]) {
    end++;
  }
  return {
    prefix: before.slice(0, start),
    removed: before.slice(start, before.length - end),
    added: after.slice(start, after.length - end),
    suffix: before.slice(before.length - end),
  };
}

const sameRow = (a: KeyValue, b: KeyValue) =>
  a.key === b.key && a.value === b.value && a.enabled === b.enabled;

/**
 * Compares two params or headers tables row by row, keeping their order. Rows are matched by a
 * longest common subsequence; a removed and an added row with the same name count as a change
 * of that row.
 */
export function diffRows(before: KeyValue[], after: KeyValue[]): RowChange[] {
  const n = before.length;
  const m = after.length;
  // lcs[i][j]: length of the longest common subsequence of before[i..] and after[j..].
  const lcs = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i][j] = sameRow(before[i], after[j])
        ? lcs[i + 1][j + 1] + 1
        : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    }
  }

  const result: RowChange[] = [];
  let removed: KeyValue[] = [];
  let added: KeyValue[] = [];
  const flush = () => {
    const unpaired = [...removed];
    const paired: RowChange[] = added.map((row) => {
      const index = unpaired.findIndex((r) => r.key === row.key);
      if (index < 0) return { kind: 'added', row };
      const [match] = unpaired.splice(index, 1);
      return { kind: 'changed', before: match, row };
    });
    result.push(...unpaired.map((row): RowChange => ({ kind: 'removed', row })), ...paired);
    removed = [];
    added = [];
  };

  let i = 0;
  let j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && sameRow(before[i], after[j])) {
      flush();
      result.push({ kind: 'same', row: after[j] });
      i++;
      j++;
    } else if (j < m && (i >= n || lcs[i][j + 1] >= lcs[i + 1][j])) {
      added.push(after[j++]);
    } else {
      removed.push(before[i++]);
    }
  }
  flush();
  return result;
}

/** The auth fields shown for an auth type, with their labels. */
export const AUTH_FIELDS: Record<
  RequestAuth['type'],
  { field: keyof RequestAuth; label: string }[]
> = {
  none: [],
  basic: [
    { field: 'username', label: 'Username' },
    { field: 'password', label: 'Password' },
  ],
  bearer: [{ field: 'token', label: 'Token' }],
  apiKey: [
    { field: 'key', label: 'Key' },
    { field: 'value', label: 'Value' },
    { field: 'in', label: 'Add to' },
  ],
};

/** The value of an auth field as the request uses it: empty when its type doesn't use the field. */
export function authValue(auth: RequestAuth, field: keyof RequestAuth): string {
  if (field === 'type') return auth.type;
  return AUTH_FIELDS[auth.type].some((f) => f.field === field) ? auth[field] : '';
}

/** The auth fields to compare: those used by either side's auth type, without repeats. */
export function authFields(
  before: RequestAuth,
  after: RequestAuth,
): { field: keyof RequestAuth; label: string }[] {
  const fields = [...AUTH_FIELDS[before.type], ...AUTH_FIELDS[after.type]];
  return fields.filter((f, index) => fields.findIndex((g) => g.field === f.field) === index);
}

export interface RequestChanges {
  name: boolean;
  line: boolean;
  params: number;
  headers: number;
  auth: boolean;
  body: boolean;
  docs: boolean;
}

/** What differs between two versions of a request, section by section. */
export function requestChanges(before: RequestContent, after: RequestContent): RequestChanges {
  const changedRows = (rows: RowChange[]) => rows.filter((r) => r.kind !== 'same').length;
  return {
    name: before.name !== after.name,
    line: before.method !== after.method || before.url !== after.url,
    params: changedRows(diffRows(before.params, after.params)),
    headers: changedRows(diffRows(before.headers, after.headers)),
    auth:
      before.auth.type !== after.auth.type ||
      authFields(before.auth, after.auth).some(
        (f) => authValue(before.auth, f.field) !== authValue(after.auth, f.field),
      ),
    body:
      before.bodyType !== after.bodyType ||
      (after.bodyType !== 'none' && before.body !== after.body),
    docs: before.description !== after.description,
  };
}
