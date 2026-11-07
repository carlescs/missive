import { Component, input, resource } from '@angular/core';
import { renderMarkdown } from '../../models/markdown';

/**
 * Markdown rendered read-only. Links are not followed (the webview would navigate away from the
 * app); their target is shown when hovering over them.
 */
@Component({
  selector: 'app-markdown-view',
  host: { class: 'block' },
  templateUrl: './markdown-view.html',
  styleUrl: './markdown-view.scss',
})
export class MarkdownView {
  readonly text = input.required<string>();

  protected readonly html = resource({
    params: () => this.text(),
    loader: ({ params }) => renderMarkdown(params),
  });

  protected stayInApp(event: MouseEvent): void {
    const link = (event.target as HTMLElement | null)?.closest('a');
    if (!link) return;
    event.preventDefault();
    if (!link.title) link.title = link.getAttribute('href') ?? '';
  }
}
