import { renderMarkdown } from './markdown';

describe('renderMarkdown', async () => {
  it('renders GitHub-flavored Markdown', async () => {
    const html = await renderMarkdown(
      '# Title\n\n- **bold** item\n\n| a | b |\n|---|---|\n| 1 | 2 |',
    );
    expect(html).toContain('<h1>Title</h1>');
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<table>');
  });

  it('shows raw HTML as text', async () => {
    const html = await renderMarkdown('<script>alert(1)</script>\n\nhi <b onclick="x()">there</b>');
    expect(html).not.toContain('<script');
    expect(html).not.toContain('<b ');
    expect(html).toContain('&lt;script&gt;');
  });

  it('never loads images', async () => {
    const html = await renderMarkdown('![diagram](https://example.com/a.png)');
    expect(html).not.toContain('<img');
    expect(html).toContain('diagram');
  });
});
