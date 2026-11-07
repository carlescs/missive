/**
 * A `{{variable}}` reference, as the host resolves it (spaces inside the braces are allowed).
 * Keep in sync with `VariableTemplate` in `src/Missive.Domain/Requests/VariableTemplate.cs`.
 * Global: reset `lastIndex` or use `matchAll`.
 */
export const VARIABLE_PATTERN = /\{\{\s*([^{}\s]+)\s*\}\}/g;

export interface TextSegment {
  text: string;
  /** The variable name when this segment is a `{{variable}}` reference. */
  variable?: string;
}

/** Splits text into plain runs and `{{variable}}` references. */
export function splitVariables(text: string): TextSegment[] {
  const segments: TextSegment[] = [];
  let last = 0;
  for (const match of text.matchAll(VARIABLE_PATTERN)) {
    if (match.index > last) segments.push({ text: text.slice(last, match.index) });
    segments.push({ text: match[0], variable: match[1] });
    last = match.index + match[0].length;
  }
  if (last < text.length) segments.push({ text: text.slice(last) });
  return segments;
}

/** What the editors know about a variable of the selected environment. */
export interface VariableInfo {
  value: string;
  secret: boolean;
  /** Whether the value was captured from a response (and overrides the environment's). */
  captured?: boolean;
}

/** The selected environment's variables by name; secrets without a local value are left out. */
export type VariableLookup = ReadonlyMap<string, VariableInfo>;

/** The CSS class for a `{{name}}` reference: undefined ones stand out when `variables` is known. */
export function variableClass(name: string, variables: VariableLookup | null): string {
  return variables && !variables.has(name) ? 'variable-undefined' : 'variable-highlight';
}

/** A one-line description of a `{{name}}` reference, for hovers. */
export function describeVariable(name: string, variables: VariableLookup | null): string {
  if (!variables) return `Variable ${name}`;
  const variable = variables.get(name);
  if (!variable) return `${name} is not defined in the selected environment`;
  const source = variable.captured ? ' (captured)' : '';
  return variable.secret
    ? `${name}: secret value${source}`
    : `${name} = ${variable.value}${source}`;
}
