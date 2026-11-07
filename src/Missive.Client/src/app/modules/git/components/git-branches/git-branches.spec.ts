import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BridgeErrorShape, GitBranch, GitStatus } from '../../../../models/messages';
import { GitStore } from '../../../../services/git-store';
import { GitBranches } from './git-branches';

const branch = (patch: Partial<GitBranch>): GitBranch => ({
  name: 'main',
  remote: null,
  current: false,
  upstream: null,
  commitId: 'a1',
  date: null,
  subject: 'Init',
  ...patch,
});

describe('GitBranches', () => {
  let git: {
    branches: ReturnType<typeof signal<GitBranch[] | null>>;
    status: ReturnType<typeof signal<GitStatus | null>>;
    busy: ReturnType<typeof signal<string | null>>;
    error: ReturnType<typeof signal<BridgeErrorShape | null>>;
    failedOperation: ReturnType<typeof signal<string | null>>;
    switchBranch: any;
    checkoutRemoteBranch: any;
    deleteBranch: any;
    createBranch: any;
    dismissError: any;
  };

  async function render() {
    TestBed.configureTestingModule({ providers: [{ provide: GitStore, useValue: git }] });
    const fixture = TestBed.createComponent(GitBranches);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[aria-expanded]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return fixture;
  }

  const button = (root: HTMLElement, label: string) =>
    root.querySelector(`[aria-label="${label}"]`) as HTMLButtonElement | null;

  beforeEach(() => {
    git = {
      branches: signal<GitBranch[] | null>([
        branch({ name: 'main', current: true, upstream: 'origin/main' }),
        branch({ name: 'old' }),
        branch({ name: 'origin/main', remote: 'origin', localName: 'main', trackedBy: 'main' }),
        branch({ name: 'origin/topic', remote: 'origin', localName: 'topic' }),
      ]),
      status: signal<GitStatus | null>({
        branch: 'main',
        upstream: 'origin/main',
        ahead: 0,
        behind: 0,
        merging: false,
        changes: [],
      }),
      busy: signal<string | null>(null),
      error: signal<BridgeErrorShape | null>(null),
      failedOperation: signal<string | null>(null),
      switchBranch: vi.fn().mockResolvedValue(true),
      checkoutRemoteBranch: vi.fn().mockResolvedValue(true),
      deleteBranch: vi.fn().mockResolvedValue(true),
      createBranch: vi.fn().mockResolvedValue(true),
      dismissError: vi.fn(),
    };
  });

  afterEach(() => vi.unstubAllGlobals());

  it('offers to switch to and delete other local branches, and to check out untracked remote ones', async () => {
    const root = (await render()).nativeElement as HTMLElement;

    expect(button(root, 'Switch to main')).toBeNull();
    expect(button(root, 'Delete main')).toBeNull();
    button(root, 'Switch to old')!.click();
    expect(git.switchBranch).toHaveBeenCalledWith('old');

    expect(button(root, 'Check out origin/main')).toBeNull();
    button(root, 'Check out origin/topic')!.click();
    expect(git.checkoutRemoteBranch).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'origin/topic' }),
    );
  });

  it('asks a second time before force-deleting an unmerged branch', async () => {
    const confirm = vi.fn().mockReturnValue(true);
    vi.stubGlobal('confirm', confirm);
    git.deleteBranch.mockImplementation(async (_: string, force?: boolean) => {
      if (force) return true;
      git.error.set({ code: 'git.branch_not_merged', message: 'not merged' });
      return false;
    });
    const fixture = await render();

    button(fixture.nativeElement, 'Delete old')!.click();
    await fixture.whenStable();

    expect(confirm).toHaveBeenCalledTimes(2);
    expect(confirm.mock.calls[1][0]).toContain('loses those commits');
    expect(git.deleteBranch).toHaveBeenNthCalledWith(1, 'old');
    expect(git.deleteBranch).toHaveBeenNthCalledWith(2, 'old', true);
  });

  it('keeps an unmerged branch when the second confirmation is declined', async () => {
    vi.stubGlobal('confirm', vi.fn().mockReturnValueOnce(true).mockReturnValueOnce(false));
    git.deleteBranch.mockImplementation(async () => {
      git.error.set({ code: 'git.branch_not_merged', message: 'not merged' });
      return false;
    });
    const fixture = await render();

    button(fixture.nativeElement, 'Delete old')!.click();
    await fixture.whenStable();

    expect(git.deleteBranch).toHaveBeenCalledTimes(1);
    expect(git.dismissError).toHaveBeenCalled();
  });

  it('creates a branch from the form', async () => {
    const fixture = await render();
    const root = fixture.nativeElement as HTMLElement;
    button(root, 'Create a branch')!.click();
    fixture.detectChanges();
    const input = button(root, 'New branch name') as unknown as HTMLInputElement;
    input.value = 'feature/login';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (root.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(git.createBranch).toHaveBeenCalledWith('feature/login', true);
  });
});
