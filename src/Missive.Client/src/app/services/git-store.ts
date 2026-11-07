import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Bridge, BridgeError } from './bridge';
import {
  BridgeErrorShape,
  ConflictResolution,
  GitBranch,
  GitBranches,
  GitConflict,
  GitFileDiff,
  GitIdentity,
  GitRemote,
  GitStatus,
  PullOutcome,
} from '../models/messages';
import { EnvironmentStore } from './environment-store';
import { WorkspaceStore } from './workspace-store';

export type GitOperation =
  | 'refresh'
  | 'init'
  | 'commit'
  | 'discard'
  | 'pull'
  | 'push'
  | 'fetch'
  | 'identity'
  | 'conflict'
  | 'resolve'
  | 'abort'
  | 'remote'
  | 'branch';

export interface GitNotice {
  kind: 'info' | 'warning';
  text: string;
}

/**
 * Git state of the open workspace. All git work happens in the .NET host; this store calls it
 * through the bridge and keeps the latest status, the running operation and its outcome.
 */
@Injectable({ providedIn: 'root' })
export class GitStore {
  private readonly bridge = inject(Bridge);
  private readonly workspace = inject(WorkspaceStore);
  private readonly environments = inject(EnvironmentStore);

  /** Whether git is reachable: the browser dev server has no git backend. */
  readonly available = this.bridge.isDesktop;
  /** The latest status, or `null` before it is loaded or when it could not be read. */
  readonly status = signal<GitStatus | null>(null);
  /** The operation in progress; only one runs at a time. */
  readonly busy = signal<GitOperation | null>(null);
  /** The error of the last operation, until the next one starts or it is dismissed. */
  readonly error = signal<BridgeErrorShape | null>(null);
  /** The operation that failed with {@link error}. */
  readonly failedOperation = signal<GitOperation | null>(null);
  /** The outcome of the last commit, pull or push. */
  readonly notice = signal<GitNotice | null>(null);
  /** The conflicted file being compared in the main area, if any. */
  readonly conflict = signal<GitConflict | null>(null);
  /** The changed file whose differences are shown in the main area, if any. */
  readonly diff = signal<GitFileDiff | null>(null);
  /** The file whose differences are being read, if any. */
  readonly diffLoading = signal<string | null>(null);
  /** Identifies the latest diff read; a stale or abandoned read's answer is ignored. */
  private diffToken = 0;
  /** The repository's remotes; `null` until they are read. */
  readonly remotes = signal<GitRemote[] | null>(null);
  /** Local and remote-tracking branches; `null` until read. */
  readonly branches = signal<GitBranch[] | null>(null);
  /** The commit identity git uses here; `null` until it is read. */
  readonly identity = signal<GitIdentity | null>(null);

  /** Whether git has no complete name and email to record on commits, so the user must enter them. */
  readonly needsIdentity = computed(() => {
    const identity = this.identity();
    return identity !== null && (!identity.name || !identity.email);
  });

  readonly hasRemotes = computed(() => (this.remotes()?.length ?? 0) > 0);

  /**
   * Open tabs with edits that are not saved to disk yet. Git only sees files, so these are not
   * committed, and a pull doesn't load newer versions into them.
   */
  readonly unsavedCount = computed(
    () =>
      this.workspace.documents().filter((d) => d.dirty).length +
      this.environments.drafts().filter((d) => d.dirty).length,
  );

  /** Whether the last status read found that the workspace folder is not inside a git repository. */
  readonly notARepository = computed(
    () => this.status() === null && this.error()?.code === 'git.not_a_repository',
  );

  readonly changeCount = computed(() => this.status()?.changes.length ?? 0);
  readonly conflicts = computed(
    () => this.status()?.changes.filter((c) => c.status === 'conflicted') ?? [],
  );

  private readonly root = computed(() => this.workspace.workspace()?.root ?? null);

  constructor() {
    if (this.available) {
      // Pick up commits and edits made in other tools when the user comes back to the window.
      window.addEventListener('focus', () => {
        if (this.root()) void this.refresh();
      });
    }

    effect(() => {
      const root = this.root();
      untracked(() => {
        this.status.set(null);
        this.error.set(null);
        this.notice.set(null);
        this.conflict.set(null);
        this.closeDiff();
        this.remotes.set(null);
        this.branches.set(null);
        this.identity.set(null);
        if (root && this.available) void this.refresh();
      });
    });

    // Keep the open diff current: close it once the file has no changes (e.g. after a commit),
    // otherwise read it again, since the status is re-read after anything that can change it.
    effect(() => {
      const status = this.status();
      untracked(() => {
        const open = this.diff();
        // A read in flight (the user opening another file) already brings the diff up to date.
        if (!open || this.diffLoading()) return;
        if (status?.changes.some((c) => c.path === open.path)) void this.loadDiff(open.path);
        else this.closeDiff();
      });
    });

    // Opening or selecting a request or environment tab (even the one shown) shows it instead.
    effect(() => {
      this.workspace.shown();
      untracked(() => this.closeDiff());
    });
  }

  async refresh(): Promise<void> {
    await this.run(
      'refresh',
      () => this.bridge.send('git.status'),
      (status) => {
        this.status.set(status);
        void this.loadRemotes();
        void this.loadBranches();
        void this.loadIdentity();
      },
    );
  }

  /** Downloads the remotes' latest commits, so the status shows what a pull would bring in. */
  async fetch(): Promise<void> {
    await this.run(
      'fetch',
      () => this.bridge.send('git.fetch'),
      (status) => {
        this.status.set(status);
        this.notice.set({
          kind: 'info',
          text: status.upstreamGone
            ? `The upstream branch ${status.upstream} was deleted on the remote. Publish to create it again.`
            : status.behind
              ? `${status.behind} new commit${status.behind === 1 ? '' : 's'} to pull.`
              : 'No new commits on the remote.',
        });
      },
    );
  }

  /** Reads the commit identity; failures leave the last one (the status shows git errors). */
  async loadIdentity(): Promise<void> {
    const root = this.root();
    if (!root) return;
    try {
      const identity = await this.bridge.send('git.identity');
      if (this.root() === root) this.identity.set(identity);
    } catch {
      // Not knowing it only hides the identity form; a commit still reports a missing identity.
    }
  }

  /** Sets the name and email recorded on commits. Resolves to whether it succeeded. */
  setIdentity(name: string, email: string, global: boolean): Promise<boolean> {
    return this.run(
      'identity',
      () => this.bridge.send('git.identity.set', { name, email, global }),
      (identity) => {
        this.identity.set(identity);
        this.notice.set({
          kind: 'info',
          text: `Commits will be recorded as ${identity.name} <${identity.email}>.`,
        });
      },
    );
  }

  /** Saves every open tab with unsaved edits, so the files git sees match the editor. */
  async saveAll(): Promise<void> {
    for (const doc of this.workspace.documents().filter((d) => d.dirty)) {
      await this.workspace.save(doc.id);
    }
    for (const draft of this.environments.drafts().filter((d) => d.dirty)) {
      await this.environments.save(draft.key);
    }
    await this.refresh();
  }

  /** Turns the workspace folder into a git repository. */
  async init(): Promise<void> {
    await this.run(
      'init',
      () => this.bridge.send('git.init'),
      (status) => {
        this.status.set(status);
        void this.loadRemotes();
        this.notice.set({ kind: 'info', text: 'Initialized a git repository.' });
        // git init may have added a .gitignore.
        void this.workspace.refresh();
      },
    );
  }

  /** Commits `paths`, or every change when `paths` is empty. Resolves to whether it succeeded. */
  async commit(message: string, paths: string[] = []): Promise<boolean> {
    const ok = await this.run(
      'commit',
      () => this.bridge.send('git.commit', { message, ...(paths.length ? { paths } : {}) }),
      (r) => {
        const completedMerge = this.status()?.merging === true;
        this.status.set(r.status);
        this.notice.set({
          kind: 'info',
          text: `${completedMerge ? 'Merge completed' : 'Committed'} (${r.commitId.slice(0, 7)}).`,
        });
      },
    );
    // Show the identity form, filled with whatever part git already knows.
    if (!ok && this.error()?.code === 'git.identity_unknown') await this.loadIdentity();
    return ok;
  }

  /** Throws away the uncommitted changes of `paths`. Resolves to whether it succeeded. */
  discard(paths: string[]): Promise<boolean> {
    const affectedPaths = new Set(paths);
    for (const change of this.status()?.changes ?? []) {
      if (paths.includes(change.path) && change.originalPath) {
        affectedPaths.add(change.originalPath);
      }
    }
    return this.run(
      'discard',
      () => this.bridge.send('git.discard', { paths }),
      (status) => {
        this.status.set(status);
        this.notice.set({
          kind: 'info',
          text: `Discarded changes in ${paths.length} file${paths.length === 1 ? '' : 's'}.`,
        });
        // Restored or deleted files may be open requests or environments.
        void this.workspace.refresh();
        void this.workspace.reloadFromDisk([...affectedPaths]);
        void this.environments.refresh([...affectedPaths]);
      },
    );
  }

  async pull(): Promise<void> {
    await this.run(
      'pull',
      () => this.bridge.send('git.pull'),
      (r) => {
        this.status.set(r.status);
        this.notice.set(PULL_NOTICES[r.outcome]);
        // A pull can add, change or remove request and environment files.
        void this.workspace.refresh();
        void this.workspace.reloadFromDisk();
        void this.environments.refresh();
      },
    );
  }

  async push(): Promise<void> {
    const ok = await this.run(
      'push',
      () => this.bridge.send('git.push'),
      (status) => {
        this.status.set(status);
        this.notice.set({ kind: 'info', text: `Pushed to ${status.upstream ?? 'the remote'}.` });
      },
    );
    // The remote has commits this clone hasn't seen: fetch them, so the count of commits to pull
    // is right and the user can pull straight away. The rejection stays on screen.
    if (!ok && this.error()?.code === 'git.push_rejected' && !this.busy()) {
      const root = this.root();
      this.busy.set('fetch');
      try {
        const status = await this.bridge.send('git.fetch');
        if (this.root() === root) this.status.set(status);
      } catch {
        // Keep the rejection; a pull fetches anyway.
      } finally {
        this.busy.set(null);
      }
    }
  }

  /** Shows both sides of a conflicted file. */
  async openConflict(path: string): Promise<void> {
    await this.run(
      'conflict',
      () => this.bridge.send('git.conflict', { path }),
      (conflict) => {
        this.closeDiff();
        this.conflict.set(conflict);
      },
    );
  }

  closeConflict(): void {
    this.conflict.set(null);
  }

  /** Shows what changed in a file since the last commit. */
  async openDiff(path: string): Promise<void> {
    this.conflict.set(null);
    await this.loadDiff(path, true);
  }

  closeDiff(): void {
    this.diffToken++;
    this.diff.set(null);
    this.diffLoading.set(null);
  }

  /** Reads a diff without blocking other git operations; only the latest read is shown. */
  private async loadDiff(path: string, report = false): Promise<void> {
    const root = this.root();
    if (!root) return;
    const token = ++this.diffToken;
    const current = () => token === this.diffToken && this.root() === root;
    this.diffLoading.set(path);
    try {
      const diff = await this.bridge.send('git.diff', { path });
      if (current()) this.diff.set(diff);
    } catch (error) {
      // A failed re-read means the file may have just stopped changing; the status closes it then.
      if (current() && report) this.reportError('refresh', error);
    } finally {
      if (current()) this.diffLoading.set(null);
    }
  }

  /** Reads the remotes; failures leave the last list (the status shows git errors). */
  async loadRemotes(): Promise<void> {
    const root = this.root();
    if (!root) return;
    try {
      const remotes = await this.bridge.send('git.remote.list');
      if (this.root() === root) this.remotes.set(remotes);
    } catch {
      if (this.root() === root) this.remotes.set(null);
    }
  }

  /** Adds a remote. Resolves to whether it succeeded. */
  addRemote(name: string, url: string): Promise<boolean> {
    return this.runRemote(
      () => this.bridge.send('git.remote.add', { name, url }),
      `Added the remote ${name.trim()}.`,
    );
  }

  /** Renames a remote and/or changes its URL. Resolves to whether it succeeded. */
  updateRemote(name: string, newName: string, url: string): Promise<boolean> {
    return this.runRemote(
      () => this.bridge.send('git.remote.update', { name, newName, url }),
      `Updated the remote ${newName.trim()}.`,
    );
  }

  /** Removes a remote. Resolves to whether it succeeded. */
  removeRemote(name: string): Promise<boolean> {
    return this.runRemote(
      () => this.bridge.send('git.remote.remove', { name }),
      `Removed the remote ${name}.`,
    );
  }

  private async runRemote(call: () => Promise<GitRemote[]>, text: string): Promise<boolean> {
    const ok = await this.run('remote', call, (remotes) => {
      this.remotes.set(remotes);
      this.notice.set({ kind: 'info', text });
    });
    // Renaming or removing a remote changes the branch's upstream.
    if (ok)
      await this.run(
        'refresh',
        () => this.bridge.send('git.status'),
        (s) => this.status.set(s),
      );
    return ok;
  }

  /** Reads the branches; failures leave the last list (the status shows git errors). */
  async loadBranches(): Promise<void> {
    const root = this.root();
    if (!root) return;
    try {
      const result = await this.bridge.send('git.branch.list');
      if (this.root() === root) this.branches.set(result.branches);
    } catch {
      if (this.root() === root) this.branches.set(null);
    }
  }

  /**
   * Checks out another local branch, then reloads the workspace and open tabs from disk. Open
   * tabs with unsaved edits keep them, after the user agrees. Resolves to whether it switched.
   */
  async switchBranch(name: string): Promise<boolean> {
    if (!this.confirmUnsavedTabs(`Switch to ${name}?`)) return false;
    return this.runBranch(
      () => this.bridge.send('git.branch.switch', { name }),
      `Switched to ${name}.`,
      true,
    );
  }

  /** Creates a branch at the current commit, optionally switching to it. */
  createBranch(name: string, switchTo: boolean): Promise<boolean> {
    const trimmed = name.trim();
    return this.runBranch(
      () => this.bridge.send('git.branch.create', { name: trimmed, switchTo }),
      switchTo ? `Created and switched to ${trimmed}.` : `Created the branch ${trimmed}.`,
      // Same commit: files don't change, but the status (branch, upstream) does.
      false,
    );
  }

  /** Checks out a remote-tracking branch as a new local branch that tracks it. */
  async checkoutRemoteBranch(branch: GitBranch): Promise<boolean> {
    const local = branch.localName ?? branch.name;
    if (!this.confirmUnsavedTabs(`Check out ${branch.name} as ${local}?`)) return false;
    return this.runBranch(
      () => this.bridge.send('git.branch.checkout', { name: branch.name }),
      `Checked out ${branch.name} as ${local}, tracking it.`,
      true,
    );
  }

  /**
   * Deletes a local branch. Without `force`, git keeps a branch whose commits aren't merged
   * anywhere; the error code is then `git.branch_not_merged`.
   */
  deleteBranch(name: string, force = false): Promise<boolean> {
    return this.runBranch(
      () => this.bridge.send('git.branch.delete', { name, force }),
      `Deleted the branch ${name}.`,
      false,
    );
  }

  private async runBranch(
    call: () => Promise<GitBranches>,
    text: string,
    reload: boolean,
  ): Promise<boolean> {
    return this.run('branch', call, (result) => {
      this.status.set(result.status);
      this.branches.set(result.branches);
      this.notice.set({ kind: 'info', text });
      if (reload) {
        // Another branch can add, change or remove request and environment files.
        void this.workspace.refresh();
        void this.workspace.reloadFromDisk();
        void this.environments.refresh();
      }
    });
  }

  /** Asks before a checkout when open tabs have unsaved edits, which git can't see. */
  private confirmUnsavedTabs(question: string): boolean {
    const unsaved = this.unsavedCount();
    if (!unsaved) return true;
    return confirm(
      `${question} ${unsaved} open tab${unsaved === 1 ? ' has' : 's have'} unsaved changes. ` +
        'They stay in their tabs, and saving them later writes them to the new branch.',
    );
  }

  /** Resolves one conflicted file the way the user chose. */
  async resolve(path: string, resolution: ConflictResolution): Promise<void> {
    await this.run(
      'resolve',
      () => this.bridge.send('git.resolve', { path, resolution }),
      (status) => {
        this.status.set(status);
        if (this.conflict()?.path === path) this.conflict.set(null);
        const remaining = status.changes.filter((c) => c.status === 'conflicted').length;
        this.notice.set({
          kind: 'info',
          text: remaining
            ? `Resolved ${path}. ${remaining} conflicted file(s) left.`
            : 'All conflicts are resolved. Commit to complete the merge.',
        });
        void this.workspace.refresh();
        void this.workspace.reloadFromDisk([path]);
        void this.environments.refresh();
      },
    );
  }

  /** Abandons the merge and restores the branch to how it was before the pull. */
  async abortMerge(): Promise<void> {
    await this.run(
      'abort',
      () => this.bridge.send('git.abortMerge'),
      (status) => {
        this.status.set(status);
        this.conflict.set(null);
        this.notice.set({ kind: 'info', text: 'The merge was aborted.' });
        void this.workspace.refresh();
        void this.workspace.reloadFromDisk();
        void this.environments.refresh();
      },
    );
  }

  dismissError(): void {
    this.error.set(null);
    this.failedOperation.set(null);
  }

  private reportError(operation: GitOperation, error: unknown): void {
    this.failedOperation.set(operation);
    this.error.set(
      error instanceof BridgeError
        ? { code: error.code, message: error.message }
        : { code: 'internal_error', message: String(error) },
    );
  }

  private async run<T>(
    operation: GitOperation,
    call: () => Promise<T>,
    onSuccess: (result: T) => void,
  ): Promise<boolean> {
    const root = this.root();
    if (!root || this.busy()) return false;

    this.busy.set(operation);
    this.error.set(null);
    this.failedOperation.set(null);
    if (operation !== 'refresh') this.notice.set(null);

    try {
      const result = await call();
      // Ignore the answer if the user switched workspaces in the meantime.
      if (this.root() !== root) return false;
      onSuccess(result);
      return true;
    } catch (error) {
      if (this.root() !== root) return false;
      this.reportError(operation, error);
      if (operation === 'refresh') this.status.set(null);
      return false;
    } finally {
      this.busy.set(null);
    }
  }
}

const PULL_NOTICES: Record<PullOutcome, GitNotice> = {
  upToDate: { kind: 'info', text: 'Already up to date.' },
  updated: { kind: 'info', text: 'Pulled the latest changes.' },
  conflicted: {
    kind: 'warning',
    text: 'The pull stopped on merge conflicts. Open each conflicted file below to resolve it.',
  },
};
