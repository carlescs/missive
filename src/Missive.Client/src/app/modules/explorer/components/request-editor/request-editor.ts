import {
  Component,
  ElementRef,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faBan, faFloppyDisk, faPaperPlane, faSpinner } from '@fortawesome/free-solid-svg-icons';
import {
  AUTH_LABELS,
  AUTH_TYPES,
  AuthType,
  BODY_LABELS,
  BODY_TYPES,
  BodyType,
  HTTP_METHODS,
  KeyValue,
  METHOD_COLORS,
  RequestAuth,
  RequestContent,
  RequestDocument,
} from '../../../../models/workspace';
import { EnvironmentStore } from '../../../../services/environment-store';
import { WorkspaceStore } from '../../../../services/workspace-store';
import { CodeEditor } from '../../../../components/code-editor/code-editor';
import { KeyValueTable } from '../../../../components/key-value-table/key-value-table';
import { ResponseViewer } from '../response-viewer/response-viewer';
import { VariableInput } from '../../../../components/variable-input/variable-input';
import { MarkdownView } from '../../../../components/markdown-view/markdown-view';

type RequestTab = 'params' | 'headers' | 'auth' | 'body' | 'docs';

@Component({
  selector: 'app-request-editor',
  imports: [
    FaIconComponent,
    KeyValueTable,
    ResponseViewer,
    CodeEditor,
    VariableInput,
    MarkdownView,
  ],
  host: { class: 'flex min-h-0 flex-1 flex-col' },
  templateUrl: './request-editor.html',
})
export class RequestEditor {
  readonly document = input.required<RequestDocument>();

  private readonly store = inject(WorkspaceStore);
  protected readonly environments = inject(EnvironmentStore);

  protected readonly activeTab = signal<RequestTab>('params');
  protected readonly tabs: { id: RequestTab; label: string }[] = [
    { id: 'params', label: 'Params' },
    { id: 'headers', label: 'Headers' },
    { id: 'auth', label: 'Auth' },
    { id: 'body', label: 'Body' },
    { id: 'docs', label: 'Docs' },
  ];

  /** Whether the Docs tab shows the Markdown source; by default only an empty description is edited. */
  private readonly documentId = computed(() => this.document().id);
  private readonly docsEditing = linkedSignal<string, boolean | null>({
    source: this.documentId,
    computation: () => null,
  });
  protected readonly editingDocs = computed(
    () => this.docsEditing() ?? !this.document().description.trim(),
  );

  protected readonly urlPlaceholder = 'https://api.example.com/users or {{baseUrl}}/users';
  protected readonly methods = HTTP_METHODS;
  protected readonly methodColors = METHOD_COLORS;
  protected readonly authTypes = AUTH_TYPES;
  protected readonly authLabels = AUTH_LABELS;
  protected readonly bodyTypes = BODY_TYPES;
  protected readonly bodyLabels = BODY_LABELS;
  protected readonly faPaperPlane = faPaperPlane;
  protected readonly faFloppyDisk = faFloppyDisk;
  protected readonly faSpinner = faSpinner;
  protected readonly faBan = faBan;
  protected readonly secretExample = '{{apiToken}}';
  protected readonly fieldClass =
    'min-w-0 rounded border border-slate-300 px-2 py-1 font-mono focus:border-sky-500 focus:outline-none';

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly responseHeight = signal<number | null>(null);
  protected readonly dragging = signal(false);

  protected startResize(event: PointerEvent): void {
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.dragging.set(true);
    event.preventDefault();
  }

  protected resize(event: PointerEvent): void {
    if (!this.dragging()) return;
    const rect = this.host.nativeElement.getBoundingClientRect();
    this.setResponseHeight(rect.bottom - event.clientY);
  }

  protected endResize(event: PointerEvent): void {
    this.dragging.set(false);
    (event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId);
  }

  protected resizeByKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    const current =
      this.responseHeight() ??
      (this.host.nativeElement.querySelector('app-response-viewer') as HTMLElement).offsetHeight;
    this.setResponseHeight(current + (event.key === 'ArrowUp' ? 24 : -24));
    event.preventDefault();
  }

  private setResponseHeight(height: number): void {
    const max = this.host.nativeElement.clientHeight - 160;
    this.responseHeight.set(Math.round(Math.max(128, Math.min(height, max))));
  }

  protected patch(patch: Partial<RequestContent>): void {
    this.store.update(this.document().id, patch);
  }

  protected patchAuth(patch: Partial<RequestAuth>): void {
    this.patch({ auth: { ...this.document().auth, ...patch } });
  }

  protected save(): void {
    void this.store.save(this.document().id);
  }

  protected send(): void {
    void this.store.send(this.document().id);
  }

  protected cancel(): void {
    void this.store.cancelSend(this.document().id);
  }

  protected setEditingDocs(editing: boolean): void {
    this.docsEditing.set(editing);
  }

  protected count(doc: RequestDocument, tab: RequestTab): number {
    const enabled = (rows: KeyValue[]) => rows.filter((r) => r.enabled && r.key).length;
    if (tab === 'params') return enabled(doc.params);
    if (tab === 'headers') return enabled(doc.headers);
    return 0;
  }
}
