import { Component, computed, inject, signal } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import {
  faChevronDown,
  faChevronRight,
  faCircleExclamation,
  faCodeBranch,
  faDownload,
  faPlus,
  faRightLeft,
  faSpinner,
  faTrash,
  faXmark,
} from '@fortawesome/free-solid-svg-icons';
import { GitBranch } from '../../../../models/messages';
import { GitStore } from '../../../../services/git-store';

/** The new-branch form's values. */
interface BranchForm {
  name: string;
  switchTo: boolean;
}

/**
 * The repository's branches, in a collapsible section of the Source control view: switch to a
 * local branch, create one from the current commit, check out a remote branch as a local
 * tracking branch, or delete a local branch. Remote branches are never deleted.
 */
@Component({
  selector: 'app-git-branches',
  imports: [FaIconComponent],
  host: { class: 'block shrink-0 border-t border-slate-200' },
  templateUrl: './git-branches.html',
})
export class GitBranches {
  protected readonly git = inject(GitStore);
  protected readonly expanded = signal(false);
  protected readonly form = signal<BranchForm | null>(null);
  /** The branch an operation is running for, to show its spinner. */
  protected readonly pending = signal<string | null>(null);

  protected readonly local = computed(() => (this.git.branches() ?? []).filter((b) => !b.remote));
  protected readonly remote = computed(() => (this.git.branches() ?? []).filter((b) => b.remote));

  protected readonly faChevronDown = faChevronDown;
  protected readonly faChevronRight = faChevronRight;
  protected readonly faCircleExclamation = faCircleExclamation;
  protected readonly faCodeBranch = faCodeBranch;
  protected readonly faDownload = faDownload;
  protected readonly faPlus = faPlus;
  protected readonly faRightLeft = faRightLeft;
  protected readonly faSpinner = faSpinner;
  protected readonly faTrash = faTrash;
  protected readonly faXmark = faXmark;

  protected startCreate(): void {
    this.expanded.set(true);
    this.form.set({ name: '', switchTo: true });
  }

  protected patch(patch: Partial<BranchForm>): void {
    this.form.update((f) => (f ? { ...f, ...patch } : f));
  }

  protected cancel(): void {
    this.form.set(null);
    if (this.git.failedOperation() === 'branch') this.git.dismissError();
  }

  protected async create(): Promise<void> {
    const f = this.form();
    if (!f || !f.name.trim()) return;
    if (await this.git.createBranch(f.name, f.switchTo)) this.form.set(null);
  }

  protected async switchTo(name: string): Promise<void> {
    await this.track(name, () => this.git.switchBranch(name));
  }

  protected async checkout(branch: GitBranch): Promise<void> {
    await this.track(branch.name, () => this.git.checkoutRemoteBranch(branch));
  }

  protected async remove(branch: GitBranch): Promise<void> {
    if (!confirm(`Delete the local branch '${branch.name}'? Nothing is deleted on the remote.`)) {
      return;
    }
    await this.track(branch.name, async () => {
      if (await this.git.deleteBranch(branch.name)) return true;
      if (this.git.error()?.code !== 'git.branch_not_merged') return false;
      // Git kept it because its commits would be lost: ask again, spelling that out.
      if (
        !confirm(
          `'${branch.name}' has commits that are not merged into any other branch or its ` +
            'upstream. Deleting it anyway loses those commits. Delete it anyway?',
        )
      ) {
        this.git.dismissError();
        return false;
      }
      return this.git.deleteBranch(branch.name, true);
    });
  }

  protected formatDate(iso: string | null): string {
    if (!iso) return '';
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString();
  }

  private async track(name: string, action: () => Promise<boolean>): Promise<void> {
    this.pending.set(name);
    try {
      await action();
    } finally {
      this.pending.set(null);
    }
  }
}
