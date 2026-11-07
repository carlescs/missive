import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, input, linkedSignal } from '@angular/core';
import { CodeDiffEditor } from '../../../../components/code-diff-editor/code-diff-editor';
import {
  AUTH_LABELS,
  BODY_LABELS,
  METHOD_COLORS,
  RequestContent,
} from '../../../../models/workspace';
import {
  EMPTY_REQUEST,
  RequestChanges,
  RowChange,
  authFields,
  authValue,
  diffRows,
  requestChanges,
  textChange,
} from '../../models/request-diff-model';
import { TextChange } from '../text-change/text-change';

type RequestTab = 'params' | 'headers' | 'auth' | 'body' | 'docs';

const ROW_STYLES: Record<RowChange['kind'], { marker: string; row: string; label: string }> = {
  same: { marker: '', row: '', label: 'Unchanged' },
  added: { marker: '+', row: 'bg-emerald-50 text-emerald-900', label: 'Added' },
  removed: { marker: '−', row: 'bg-rose-50 text-rose-900 line-through', label: 'Removed' },
  changed: { marker: '~', row: 'bg-amber-50', label: 'Changed' },
};

/**
 * Two versions of a request laid out like the request editor, read-only, with what changed
 * marked: the name, the method and URL, and each section's rows and fields.
 */
@Component({
  selector: 'app-request-diff',
  imports: [NgTemplateOutlet, TextChange, CodeDiffEditor],
  host: { class: 'flex min-h-0 flex-1 flex-col' },
  templateUrl: './request-diff.html',
})
export class RequestDiff {
  /** The committed request; `null` for a new request file. */
  readonly before = input.required<RequestContent | null>();
  /** The request in the working tree; `null` for a deleted request file. */
  readonly after = input.required<RequestContent | null>();
  /** How the body differences are shown. */
  readonly sideBySide = input(true);

  protected readonly empty = EMPTY_REQUEST;
  protected readonly authLabels = AUTH_LABELS;
  protected readonly bodyLabels = BODY_LABELS;
  protected readonly tabs: { id: RequestTab; label: string }[] = [
    { id: 'params', label: 'Params' },
    { id: 'headers', label: 'Headers' },
    { id: 'auth', label: 'Auth' },
    { id: 'body', label: 'Body' },
    { id: 'docs', label: 'Docs' },
  ];

  private readonly a = computed(() => this.before() ?? EMPTY_REQUEST);
  private readonly b = computed(() => this.after() ?? EMPTY_REQUEST);

  // Compared by value, so re-reading the same diff (e.g. on window focus) keeps the open tab.
  protected readonly changes = computed(() => requestChanges(this.a(), this.b()), {
    equal: (x, y) => JSON.stringify(x) === JSON.stringify(y),
  });
  // For an added or deleted request every row shows as added or removed.
  protected readonly paramRows = computed(() => diffRows(this.a().params, this.b().params));
  protected readonly headerRows = computed(() => diffRows(this.a().headers, this.b().headers));
  protected readonly auth = computed(() => {
    const a = this.a().auth;
    const b = this.b().auth;
    return authFields(a, b).map((f) => ({
      ...f,
      before: this.before() ? authValue(a, f.field) : authValue(b, f.field),
      after: this.after() ? authValue(b, f.field) : authValue(a, f.field),
    }));
  });

  /** Opens on the first section that changed, again whenever the compared requests change. */
  protected readonly activeTab = linkedSignal<RequestTab>(() => {
    const c = this.changes();
    return this.tabs.find((t) => this.changeCount(c, t.id))?.id ?? 'params';
  });

  protected split = textChange;

  protected methodColor(request: RequestContent): string {
    return METHOD_COLORS[request.method];
  }

  protected rowStyle(change: RowChange): (typeof ROW_STYLES)[RowChange['kind']] {
    return ROW_STYLES[change.kind];
  }

  protected changeCount(c: RequestChanges, tab: RequestTab): number | boolean {
    return c[tab];
  }
}
