import type { Marked } from 'marked';

const escapeHtml = (text: string) =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

let parser: Promise<Marked> | null = null;

/**
 * GitHub-flavored Markdown for request descriptions, loaded on first use so the parser stays out
 * of the initial bundle. Raw HTML is shown as text rather than rendered, and images as their alt
 * text: the app never loads anything from the network.
 */
function loadParser(): Promise<Marked> {
  parser ??= import('marked').then(
    ({ Marked }) =>
      new Marked({
        gfm: true,
        renderer: {
          html: (token) => escapeHtml(token.text),
          image: (token) =>
            `<span class="md-image" title="${escapeHtml(token.href)}">${escapeHtml(token.text || 'image')}</span>`,
        },
      }),
  );
  return parser;
}

/** Renders Markdown to HTML. Bind the result with `[innerHTML]`, which sanitizes it as well. */
export async function renderMarkdown(text: string): Promise<string> {
  return (await loadParser()).parse(text, { async: false });
}
