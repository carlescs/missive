import { Component, input, output } from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faTrash } from '@fortawesome/free-solid-svg-icons';
import { KeyValue } from '../../models/workspace';

/**
 * Editable key/value list (query params, headers, captures). Always shows one empty row for new
 * entries.
 */
@Component({
  selector: 'app-key-value-table',
  imports: [FaIconComponent],
  templateUrl: './key-value-table.html',
})
export class KeyValueTable {
  readonly rows = input.required<KeyValue[]>();
  readonly keyLabel = input('Key');
  readonly valueLabel = input('Value');
  readonly valuePlaceholder = input<string | null>(null);
  /** Whether rows can be turned off without deleting them. */
  readonly toggleable = input(true);
  readonly rowsChange = output<KeyValue[]>();

  protected readonly faTrash = faTrash;

  protected rowsWithBlank(): KeyValue[] {
    return [...this.rows(), { key: '', value: '', enabled: true }];
  }

  protected patch(index: number, patch: Partial<KeyValue>): void {
    const rows = [...this.rows()];
    const current = rows[index] ?? { key: '', value: '', enabled: true };
    rows[index] = { ...current, ...patch };
    this.rowsChange.emit(rows);
  }

  protected remove(index: number): void {
    this.rowsChange.emit(this.rows().filter((_, i) => i !== index));
  }
}
