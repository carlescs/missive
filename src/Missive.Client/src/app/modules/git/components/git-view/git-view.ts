import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import {
  faArrowDown,
  faArrowUp,
  faBan,
  faCheck,
  faCircleExclamation,
  faCloudArrowDown,
  faFloppyDisk,
  faUser,
  faChevronDown,
  faChevronRight,
  faCodeBranch,
  faFolder,
  faFolderOpen,
  faListUl,
  faRotateLeft,
  faSitemap,
  faRotate,
  faSpinner,
  faTriangleExclamation,
  faXmark,
} from '@fortawesome/free-solid-svg-icons';
import { GitChange } from '../../../../models/messages';
import { STATUS_BADGES, fileName, folderOf } from '../../models/git-format';
import { GitHistoryStore } from '../../../../services/git-history-store';
import { GitOperation, GitStore } from '../../../../services/git-store';
import { WorkspaceStore } from '../../../../services/workspace-store';
import { GitHistoryList } from '../git-history-list/git-history-list';
import { GitRemotes } from '../git-remotes/git-remotes';
import { GitBranches } from '../git-branches/git-branches';

@Component({
  selector: 'app-git-view',
  imports: [FaIconComponent, GitHistoryList, GitRemotes, GitBranches],
  host: { class: 'flex min-h-0 flex-1 flex-col' },
  templateUrl: './git-view.html',
})
export class GitView {
  protected readonly git = inject(GitStore);
  protected readonly workspace = inject(WorkspaceStore);
  private readonly history = inject(GitHistoryStore);
  protected readonly badges = STATUS_BADGES;
  protected readonly message = signal('');
  /** Whether the list shows the working tree's changes or the commit history. */
  protected readonly tab = signal<'changes' | 'history'>('changes');
  /** Whether changes are listed flat, with their folder, or grouped under folders. */
  protected readonly viewMode = signal<'list' | 'tree'>('list');
  /** Tree folders the user collapsed, by path. */
  protected readonly collapsed = signal(new Set<string>());
  protected readonly fileName = fileName;
  protected readonly folder = folderOf;

  protected showHistory(): void {
    this.tab.set('history');
    if (this.history.commits() === null) void this.history.load();
  }

  protected tabClass(tab: 'changes' | 'history'): string {
    return this.tab() === tab
      ? 'border-sky-600 text-slate-800'
      : 'border-transparent text-slate-500 hover:text-slate-800';
  }

  constructor() {
    // Prefill the identity form with whatever part of the identity git already knows.
    effect(() => {
      const identity = this.git.identity();
      untracked(() => {
        if (identity?.name && !this.identityName()) this.identityName.set(identity.name);
        if (identity?.email && !this.identityEmail()) this.identityEmail.set(identity.email);
      });
    });
    // Offer git's merge commit message once a merge starts, unless the user typed one already.
    effect(() => {
      const mergeMessage = this.git.status()?.mergeMessage;
      if (mergeMessage && !untracked(this.message).trim()) this.message.set(mergeMessage);
    });
  }

  protected abortMerge(): void {
    if (
      confirm(
        'Abort the merge? Your branch goes back to how it was before the pull, and any conflict resolutions so far are lost.',
      )
    ) {
      void this.git.abortMerge();
    }
  }
  /** Paths picked for a partial commit; empty commits everything. */
  protected readonly selected = signal(new Set<string>());

  /** Selected paths that still have changes. */
  protected readonly selectedPaths = computed(() => {
    const changed = new Set(this.git.status()?.changes.map((c) => c.path));
    return [...this.selected()].filter((p) => changed.has(p));
  });

  protected readonly canCommit = computed(
    () =>
      this.git.busy() === null &&
      this.message().trim().length > 0 &&
      this.git.changeCount() > 0 &&
      this.git.conflicts().length === 0,
  );

  protected readonly rows = computed<ChangeRow[]>(() => {
    const changes = this.git.status()?.changes ?? [];
    if (this.viewMode() === 'list') {
      return changes.map((change) => ({
        key: `file:${change.status}:${change.path}`,
        depth: 0,
        name: '',
        change,
      }));
    }
    return buildTree(changes, this.collapsed());
  });

  protected readonly allPaths = computed(() => this.git.status()?.changes.map((c) => c.path) ?? []);

  protected readonly faChevronDown = faChevronDown;
  protected readonly faChevronRight = faChevronRight;
  protected readonly faFolder = faFolder;
  protected readonly faListUl = faListUl;
  protected readonly faRotateLeft = faRotateLeft;
  protected readonly faSitemap = faSitemap;
  protected readonly faCodeBranch = faCodeBranch;
  protected readonly faArrowDown = faArrowDown;
  protected readonly faArrowUp = faArrowUp;
  protected readonly faBan = faBan;
  protected readonly faCheck = faCheck;
  protected readonly faRotate = faRotate;
  protected readonly faSpinner = faSpinner;
  protected readonly faFolderOpen = faFolderOpen;
  protected readonly faXmark = faXmark;
  protected readonly faCircleExclamation = faCircleExclamation;
  protected readonly faTriangleExclamation = faTriangleExclamation;
  protected readonly faCloudArrowDown = faCloudArrowDown;
  protected readonly faFloppyDisk = faFloppyDisk;
  protected readonly faUser = faUser;

  /** The identity form's fields, prefilled with any part git already knows. */
  protected readonly identityName = signal('');
  protected readonly identityEmail = signal('');
  protected readonly identityGlobal = signal(true);

  /** A branch without an upstream can pull once a remote is set: from its namesake there. */
  protected readonly canPull = computed(() => {
    const status = this.git.status();
    if (!status?.branch || status.upstreamGone) return false;
    return !!status.upstream || this.git.hasRemotes();
  });

  protected readonly pullTitle = computed(() => {
    const status = this.git.status();
    if (!status?.branch) return 'No branch is checked out';
    if (status.upstreamGone) return `${status.upstream} was deleted on the remote`;
    if (status.upstream) return 'Pull from ' + status.upstream;
    return this.git.hasRemotes()
      ? `Pull ${status.branch} from the remote`
      : 'Add a remote to pull from';
  });

  protected readonly pushTitle = computed(() => {
    const status = this.git.status();
    if (!status?.branch) return 'No branch is checked out';
    if (status.unborn) return 'Commit your changes before publishing the branch';
    if (status.upstreamGone) return 'Publish the branch again';
    return status.upstream ? 'Push to ' + status.upstream : 'Publish the branch';
  });

  protected async saveIdentity(): Promise<void> {
    await this.git.setIdentity(this.identityName(), this.identityEmail(), this.identityGlobal());
  }

  /** Pulls, after a warning when unsaved tabs could later overwrite what the pull brings in. */
  protected pull(): void {
    const unsaved = this.git.unsavedCount();
    if (
      unsaved &&
      !confirm(
        `${unsaved} open tab(s) have unsaved changes. The pull won't update them, and saving them afterwards would overwrite the pulled versions. Pull anyway?`,
      )
    ) {
      return;
    }
    void this.git.pull();
  }

  protected async commit(): Promise<void> {
    if (!this.canCommit()) return;
    if (await this.git.commit(this.message(), this.selectedPaths())) {
      this.message.set('');
      this.selected.set(new Set());
    }
  }

  protected async discard(paths: string[]): Promise<void> {
    const conflicted = new Set(this.git.conflicts().map((c) => c.path));
    const targets = paths.filter((p) => !conflicted.has(p));
    if (!targets.length || this.git.busy() !== null) return;
    const what =
      targets.length === 1
        ? `your changes to ${targets[0]}`
        : `your changes to ${targets.length} files`;
    if (!confirm(`Discard ${what}? This cannot be undone.`)) return;
    if (await this.git.discard(targets)) {
      this.selected.update((set) => new Set([...set].filter((p) => !targets.includes(p))));
    }
  }

  protected toggleFolder(key: string): void {
    this.collapsed.update((set) => {
      const next = new Set(set);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  protected toggle(path: string): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }

  protected busyIcon(operation: GitOperation, icon: typeof faCheck): typeof faCheck {
    return this.git.busy() === operation ? faSpinner : icon;
  }

  protected spin(operation: GitOperation): 'spin' | undefined {
    return this.git.busy() === operation ? 'spin' : undefined;
  }

  protected describe(change: GitChange): string {
    return change.originalPath ? `${change.originalPath} → ${change.path}` : change.path;
  }
}

/** A line of the changes list: a file, or (in the tree view) a folder heading. */
interface ChangeRow {
  key: string;
  depth: number;
  name: string;
  change?: GitChange;
}

/** Groups changes under their folders, folders first, skipping the contents of collapsed ones. */
function buildTree(changes: GitChange[], collapsed: Set<string>): ChangeRow[] {
  interface Node {
    folders: Map<string, Node>;
    files: GitChange[];
  }
  const root: Node = { folders: new Map(), files: [] };
  for (const change of changes) {
    const parts = change.path.split('/');
    let node = root;
    for (const part of parts.slice(0, -1)) {
      let next = node.folders.get(part);
      if (!next) node.folders.set(part, (next = { folders: new Map(), files: [] }));
      node = next;
    }
    node.files.push(change);
  }

  const rows: ChangeRow[] = [];
  const walk = (node: Node, prefix: string, depth: number): void => {
    for (const [name, child] of [...node.folders].sort(([a], [b]) => a.localeCompare(b))) {
      const path = prefix + name;
      const key = `folder:${path}`;
      rows.push({ key, depth, name });
      if (!collapsed.has(path)) walk(child, path + '/', depth + 1);
    }
    for (const change of [...node.files].sort((a, b) =>
      fileName(a.path).localeCompare(fileName(b.path)),
    )) {
      rows.push({
        key: `file:${change.status}:${change.path}`,
        depth,
        name: fileName(change.path),
        change,
      });
    }
  };
  walk(root, '', 0);
  return rows;
}
