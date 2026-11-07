import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CapturedValueDto } from '../../../../models/messages';
import { EnvironmentStore } from '../../../../services/environment-store';
import { WorkspaceStore } from '../../../../services/workspace-store';
import { CapturedValues } from './captured-values';

const token: CapturedValueDto = { name: 'token', value: 't0k' };
const etag: CapturedValueDto = { name: 'etag', value: '"v1"' };

describe('CapturedValues', () => {
  let store: { capturedValues: any; clearCaptured: any };
  const capturesStored = signal(0);

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: EnvironmentStore, useValue: store },
        { provide: WorkspaceStore, useValue: { capturesStored } },
      ],
    });
    const fixture = TestBed.createComponent(CapturedValues);
    fixture.componentRef.setInput('environmentId', 'dev.yaml');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    store = {
      capturedValues: vi.fn().mockResolvedValue([etag, token]),
      clearCaptured: vi.fn().mockResolvedValue([etag]),
    };
  });

  afterEach(() => vi.unstubAllGlobals());

  it('lists the captured values and reloads after a send stores more', async () => {
    const fixture = await render();
    expect(store.capturedValues).toHaveBeenCalledWith('dev.yaml');
    expect(fixture.nativeElement.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(fixture.nativeElement.textContent).toContain('t0k');

    capturesStored.update((n) => n + 1);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(store.capturedValues).toHaveBeenCalledTimes(2);
  });

  it('forgets one value', async () => {
    const fixture = await render();
    fixture.nativeElement.querySelector('[aria-label="Forget token"]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.clearCaptured).toHaveBeenCalledWith('dev.yaml', 'token');
    expect(fixture.nativeElement.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('clears every value after asking', async () => {
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    store.clearCaptured.mockResolvedValue([]);
    const fixture = await render();
    const clear = [...fixture.nativeElement.querySelectorAll('button')].find((b: HTMLElement) =>
      b.textContent?.includes('Clear all'),
    ) as HTMLButtonElement;

    clear.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.clearCaptured).toHaveBeenCalledWith('dev.yaml');
    expect(fixture.nativeElement.textContent).toContain('Nothing captured yet');
  });
});
