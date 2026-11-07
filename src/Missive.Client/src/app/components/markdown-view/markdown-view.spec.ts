import { TestBed } from '@angular/core/testing';
import { MarkdownView } from './markdown-view';

describe('MarkdownView', () => {
  it('renders the text and keeps links from navigating', async () => {
    const fixture = TestBed.createComponent(MarkdownView);
    fixture.componentRef.setInput('text', 'See [the docs](https://example.com/docs).');
    await fixture.whenStable();
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector('a')!;
    expect(link.textContent).toBe('the docs');
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
    expect(link.title).toBe('https://example.com/docs');
  });
});
