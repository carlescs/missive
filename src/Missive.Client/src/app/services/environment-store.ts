import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Bridge, BridgeError } from './bridge';
import {
  CapturedValueDto,
  CookieDto,
  EnvironmentSummaryDto,
  EnvironmentVariableDto,
} from '../models/messages';
import { VariableInfo, VariableLookup } from '../models/variables';
import { WorkspaceStore } from './workspace-store';

/** An environment open in an editor tab. */
export interface EnvironmentDraft {
  /** The environment's file name; `null` for a new environment that has not been saved yet. */
  id: string | null;
  /** The draft's tab key (see `WorkspaceStore.tabs`); unlike `id`, never changes. */
  key: string;
  name: string;
  variables: EnvironmentVariableDto[];
  /** Whether requests sent with it keep cookies between them. */
  cookieJar: boolean;
  dirty: boolean;
  saving: boolean;
  /** Incremented on every edit, so a save can tell whether the draft changed meanwhile. */
  revision: number;
}

/**
 * The open workspace's environments: the list, the selected one (whose variables requests are
 * sent with) and the environments open in editor tabs, next to the request tabs. Files are read
 * and written by the .NET host.
 */
@Injectable({ providedIn: 'root' })
export class EnvironmentStore {
  private readonly bridge = inject(Bridge);
  private readonly workspace = inject(WorkspaceStore);

  readonly environments = signal<EnvironmentSummaryDto[]>([]);
  readonly activeId = signal<string | null>(null);
  readonly active = computed(
    () => this.environments().find((e) => e.id === this.activeId()) ?? null,
  );
  /** The selected environment's variables, for highlighting and hovers in the editors. */
  readonly variables = signal<VariableLookup>(new Map());
  /** The environments open in editor tabs. */
  readonly drafts = signal<EnvironmentDraft[]>([]);
  /** The environment in the active tab, if an environment tab is active. */
  readonly draft = computed(
    () => this.drafts().find((d) => d.key === this.workspace.activeTab()) ?? null,
  );

  private readonly root = computed(() => this.workspace.workspace()?.root ?? null);
  private loadCount = 0;

  constructor() {
    effect(() => {
      const root = this.root();
      untracked(() => {
        this.environments.set([]);
        this.activeId.set(null);
        this.variables.set(new Map());
        this.workspace.removeTabs(this.drafts().map((d) => d.key));
        this.drafts.set([]);
        if (root) void this.refresh();
      });
    });
    // Sending a request that captured values changes the selected environment's variables.
    effect(() => {
      if (this.workspace.capturesStored() === 0) return;
      untracked(() => void this.loadVariables(++this.loadCount));
    });
  }

  /** Re-reads the list, selected variables, and clean open drafts from disk. */
  async refresh(paths?: string[]): Promise<void> {
    const root = this.root();
    if (!root) return;
    const load = ++this.loadCount;
    try {
      const list = await this.bridge.send('environment.list');
      if (load !== this.loadCount || this.root() !== root) return;
      this.environments.set(list.environments);
      this.activeId.set(list.active);
      await this.reloadDrafts(list.environments, paths, load, root);
      await this.loadVariables(load);
    } catch (error) {
      this.report(error);
    }
  }

  /** Selects the environment requests are sent with, or none. */
  async select(id: string | null): Promise<void> {
    if (id === this.activeId()) return;
    try {
      await this.bridge.send('environment.select', { id });
      this.activeId.set(id);
      await this.loadVariables(++this.loadCount);
    } catch (error) {
      this.report(error);
      await this.refresh();
    }
  }

  /** Opens an environment in a tab, or shows its tab if it is already open. */
  async edit(id: string): Promise<void> {
    if (this.showOpen(id)) return;
    const root = this.root();
    try {
      const environment = await this.bridge.send('environment.load', { id });
      // Another click may have opened it while loading, or another folder been opened.
      if (this.showOpen(id) || this.root() !== root) return;
      const key = this.workspace.newTabKey();
      this.drafts.update((drafts) => [
        ...drafts,
        {
          ...environment,
          cookieJar: !!environment.cookieJar,
          id,
          key,
          dirty: false,
          saving: false,
          revision: 0,
        },
      ]);
      this.workspace.openTab(key);
    } catch (error) {
      this.report(error);
    }
  }

  /** Opens a new, unsaved environment in a tab. */
  create(): void {
    const key = this.workspace.newTabKey();
    this.drafts.update((drafts) => [
      ...drafts,
      {
        id: null,
        key,
        name: 'New environment',
        variables: [],
        cookieJar: false,
        dirty: true,
        saving: false,
        revision: 0,
      },
    ]);
    this.workspace.openTab(key);
  }

  update(
    key: string,
    patch: Partial<Pick<EnvironmentDraft, 'name' | 'variables' | 'cookieJar'>>,
  ): void {
    this.patchDraft(key, (d) => ({ ...d, ...patch, dirty: true, revision: d.revision + 1 }));
  }

  /** Writes a draft to its file (or creates the file); a new environment is selected if none is. */
  async save(key: string): Promise<void> {
    const draft = this.drafts().find((d) => d.key === key);
    if (!draft || draft.saving) return;
    this.patchDraft(key, (d) => ({ ...d, saving: true }));
    try {
      const environment = {
        name: draft.name,
        variables: draft.variables,
        cookieJar: draft.cookieJar,
      };
      const saved = draft.id
        ? await this.bridge.send('environment.save', { id: draft.id, environment })
        : await this.bridge.send('environment.create', { environment });

      this.patchDraft(key, (d) => {
        const base = { ...d, id: saved.id, saving: false };
        // Keep edits typed while the save was in flight; they still need saving.
        return d.revision === draft.revision
          ? {
              ...base,
              ...saved.environment,
              cookieJar: !!saved.environment.cookieJar,
              dirty: false,
            }
          : base;
      });
      this.workspace.dismissError();
      if (!draft.id && !this.activeId()) {
        await this.select(saved.id);
      }
      await this.refresh();
    } catch (error) {
      this.patchDraft(key, (d) => ({ ...d, saving: false }));
      this.report(error);
    }
  }

  /** Deletes a draft's environment, after asking the user, and closes its tab. */
  async deleteDraft(key: string): Promise<void> {
    const draft = this.drafts().find((d) => d.key === key);
    if (!draft) return;
    if (!draft.id) {
      this.remove(key);
      return;
    }
    if (
      !confirm(
        `Delete the environment "${draft.name}" and the secret values and cookies stored for it?`,
      )
    ) {
      return;
    }
    try {
      await this.bridge.send('environment.delete', { id: draft.id });
      this.remove(key);
      await this.refresh();
    } catch (error) {
      this.report(error);
    }
  }

  /** The cookies kept for an environment; `null` when they can't be read (the error is reported). */
  async cookies(id: string): Promise<CookieDto[] | null> {
    try {
      return await this.bridge.send('cookie.list', { environment: id });
    } catch (error) {
      this.report(error);
      return null;
    }
  }

  /** Deletes one cookie from an environment's jar; returns the cookies left, or `null` on failure. */
  async deleteCookie(id: string, cookie: CookieDto): Promise<CookieDto[] | null> {
    try {
      return await this.bridge.send('cookie.delete', {
        environment: id,
        name: cookie.name,
        domain: cookie.domain,
        path: cookie.path,
      });
    } catch (error) {
      this.report(error);
      return null;
    }
  }

  /** Deletes every cookie kept for an environment; returns whether it worked. */
  async clearCookies(id: string): Promise<boolean> {
    try {
      await this.bridge.send('cookie.clear', { environment: id });
      return true;
    } catch (error) {
      this.report(error);
      return false;
    }
  }

  /** The values requests captured for an environment, or `null` when they can't be read. */
  async capturedValues(id: string): Promise<CapturedValueDto[] | null> {
    try {
      return await this.bridge.send('capture.list', { environment: id });
    } catch (error) {
      this.report(error);
      return null;
    }
  }

  /**
   * Forgets one captured value of an environment, or all of them without `name`; returns what is
   * left, or `null` on failure.
   */
  async clearCaptured(id: string, name?: string): Promise<CapturedValueDto[] | null> {
    try {
      const left = await this.bridge.send('capture.clear', { environment: id, name });
      if (id === this.activeId()) await this.loadVariables(++this.loadCount);
      return left;
    } catch (error) {
      this.report(error);
      return null;
    }
  }

  /** Closes a draft's tab, after asking if it has unsaved changes. */
  close(key: string): void {
    const draft = this.drafts().find((d) => d.key === key);
    if (!draft) return;
    if (
      draft.dirty &&
      !confirm(`Discard unsaved changes to the environment "${draft.name || 'Untitled'}"?`)
    ) {
      return;
    }
    this.remove(key);
  }

  /** Shows the tab of the environment `id` if it is open; returns whether it was. */
  private showOpen(id: string): boolean {
    const open = this.drafts().find((d) => d.id === id);
    if (open) this.workspace.activateTab(open.key);
    return !!open;
  }

  private remove(key: string): void {
    this.drafts.update((drafts) => drafts.filter((d) => d.key !== key));
    this.workspace.removeTabs([key]);
  }

  private patchDraft(key: string, change: (draft: EnvironmentDraft) => EnvironmentDraft): void {
    this.drafts.update((drafts) => drafts.map((d) => (d.key === key ? change(d) : d)));
  }

  private async reloadDrafts(
    environments: EnvironmentSummaryDto[],
    paths: string[] | undefined,
    load: number,
    root: string,
  ): Promise<void> {
    const available = new Set(environments.map((environment) => environment.id));
    const targets = this.drafts().filter(
      (draft) => draft.id && (!paths || paths.includes(`environments/${draft.id}`)),
    );
    for (const draft of targets) {
      if (load !== this.loadCount || this.root() !== root) return;
      if (!available.has(draft.id!)) {
        const current = this.drafts().find((candidate) => candidate.key === draft.key);
        if (!current || current.id !== draft.id) continue;
        if (current.dirty) this.patchDraft(draft.key, (candidate) => ({ ...candidate, id: null }));
        else this.remove(draft.key);
        continue;
      }
      if (draft.dirty) continue;
      try {
        const environment = await this.bridge.send('environment.load', { id: draft.id! });
        if (load !== this.loadCount || this.root() !== root) return;
        this.patchDraft(draft.key, (current) =>
          current.id === draft.id && !current.dirty
            ? {
                ...current,
                ...environment,
                cookieJar: !!environment.cookieJar,
                revision: current.revision + 1,
              }
            : current,
        );
      } catch {
        // Keep an open draft if its file exists but cannot currently be read.
      }
    }
  }

  private async loadVariables(load: number): Promise<void> {
    const id = this.activeId();
    let variables: VariableLookup = new Map();
    if (id) {
      try {
        const environment = await this.bridge.send('environment.load', { id });
        const lookup = new Map<string, VariableInfo>(
          environment.variables
            .filter((v) => !v.secret || v.value)
            .map((v) => [v.name, { value: v.value, secret: v.secret }]),
        );
        // Captured values take precedence, as when sending.
        const secret = new Set(environment.variables.filter((v) => v.secret).map((v) => v.name));
        try {
          for (const c of await this.bridge.send('capture.list', { environment: id })) {
            lookup.set(c.name, { value: c.value, secret: secret.has(c.name), captured: true });
          }
        } catch {
          // Unreadable captured values are reported when sending.
        }
        variables = lookup;
      } catch {
        // An unreadable environment defines nothing; sending explains why.
      }
    }
    if (load === this.loadCount) this.variables.set(variables);
  }

  private report(error: unknown): void {
    this.workspace.error.set(
      error instanceof BridgeError || error instanceof Error ? error.message : String(error),
    );
  }
}
