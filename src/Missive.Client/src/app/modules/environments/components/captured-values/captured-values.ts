import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faFileImport, faRotate, faTrash } from '@fortawesome/free-solid-svg-icons';
import { CapturedValueDto } from '../../../../models/messages';
import { EnvironmentStore } from '../../../../services/environment-store';
import { WorkspaceStore } from '../../../../services/workspace-store';

/** Lists the values requests captured for an environment and lets the user forget them. */
@Component({
  selector: 'app-captured-values',
  imports: [FaIconComponent],
  templateUrl: './captured-values.html',
})
export class CapturedValues {
  private readonly store = inject(EnvironmentStore);
  private readonly workspace = inject(WorkspaceStore);

  /** The environment's id (its file name). */
  readonly environmentId = input.required<string>();

  protected readonly values = signal<CapturedValueDto[]>([]);
  protected readonly loading = signal(false);

  protected readonly faFileImport = faFileImport;
  protected readonly faRotate = faRotate;
  protected readonly faTrash = faTrash;

  private loadCount = 0;

  constructor() {
    // Reload when the environment changes, and after a send stored new values.
    effect(() => {
      const id = this.environmentId();
      this.workspace.capturesStored();
      untracked(() => void this.load(id));
    });
  }

  async refresh(): Promise<void> {
    await this.load(this.environmentId());
  }

  protected async remove(value: CapturedValueDto): Promise<void> {
    const id = this.environmentId();
    const left = await this.store.clearCaptured(id, value.name);
    if (left && id === this.environmentId()) this.values.set(left);
  }

  protected async clear(): Promise<void> {
    const id = this.environmentId();
    if (!confirm('Forget every value captured for this environment?')) return;
    const left = await this.store.clearCaptured(id);
    if (left && id === this.environmentId()) this.values.set(left);
  }

  private async load(id: string): Promise<void> {
    const load = ++this.loadCount;
    this.loading.set(true);
    const values = await this.store.capturedValues(id);
    if (load !== this.loadCount) return;
    this.loading.set(false);
    if (values) this.values.set(values);
  }
}
