import { TestBed } from '@angular/core/testing';
import { Mock } from 'vitest';
import { GitBranch, GitStatus, WorkspaceDto } from '../models/messages';
import { Bridge, BridgeError } from './bridge';
import { GitStore } from './git-store';
import { WorkspaceStore } from './workspace-store';

const workspace = { name: 'api', root: '/home/me/api', nodes: [] } as unknown as WorkspaceDto;

const status = (patch: Partial<GitStatus> = {}): GitStatus => ({
  branch: 'main',
  upstream: 'origin/main',
  ahead: 0,
  behind: 0,
  merging: false,
  changes: [],
  ...patch,
});

const main: GitBranch = {
  name: 'main',
  remote: null,
  current: true,
  upstream: 'origin/main',
  commitId: 'a1',
  date: null,
  subject: 'Init',
};
const feature: GitBranch = { ...main, name: 'feature', current: false, upstream: null };

describe('GitStore', () => {
  let send: Mock;
  let handlers: Record<string, (payload: any) => unknown>;
  let store: GitStore;
  let workspaceStore: WorkspaceStore;

  beforeEach(async () => {
    handlers = {
      'git.status': () => status(),
      'git.remote.list': () => [{ name: 'origin', fetchUrl: 'u', pushUrl: 'u' }],
      'git.branch.list': () => ({ status: status(), branches: [main] }),
      'git.identity': () => ({ name: 'Ada', email: 'ada@example.com' }),
      'workspace.tree': () => workspace,
    };
    send = vi.fn(async (type: string, payload: unknown) => {
      const handler = handlers[type];
      if (!handler) throw new Error(`Unexpected message ${type}`);
      return handler(payload);
    });
    TestBed.configureTestingModule({
      providers: [{ provide: Bridge, useValue: { isDesktop: true, send } }],
    });
    store = TestBed.inject(GitStore);
    workspaceStore = TestBed.inject(WorkspaceStore);
    workspaceStore.workspace.set({ name: 'api', root: '/home/me/api' } as never);
    TestBed.tick();
    await vi.waitFor(() => expect(store.identity()).not.toBeNull());
  });

  it('loads the remotes and identity with the status', () => {
    expect(store.hasRemotes()).toBe(true);
    expect(store.needsIdentity()).toBe(false);
  });

  it('asks for an identity when git has none', async () => {
    handlers['git.identity'] = () => ({ name: null, email: null });
    await store.refresh();
    await vi.waitFor(() => expect(store.needsIdentity()).toBe(true));
  });

  it('stores the identity the user enters', async () => {
    handlers['git.identity.set'] = (p) => ({ name: p.name, email: p.email });

    expect(await store.setIdentity('Bea', 'bea@example.com', false)).toBe(true);

    expect(send).toHaveBeenCalledWith('git.identity.set', {
      name: 'Bea',
      email: 'bea@example.com',
      global: false,
    });
    expect(store.identity()).toEqual({ name: 'Bea', email: 'bea@example.com' });
  });

  it('reloads the identity when a commit fails for lack of one', async () => {
    handlers['git.commit'] = () => {
      throw new BridgeError('git.identity_unknown', 'Who are you?');
    };
    handlers['git.identity'] = () => ({ name: 'Ada', email: null });

    expect(await store.commit('Add users')).toBe(false);

    expect(store.error()?.code).toBe('git.identity_unknown');
    expect(store.needsIdentity()).toBe(true);
  });

  it('fetches and reports the commits to pull', async () => {
    handlers['git.fetch'] = () => status({ behind: 2 });

    await store.fetch();

    expect(store.status()?.behind).toBe(2);
    expect(store.notice()?.text).toBe('2 new commits to pull.');
  });

  it('fetches after a rejected push, keeping the rejection', async () => {
    handlers['git.push'] = () => {
      throw new BridgeError('git.push_rejected', 'Pull first.');
    };
    handlers['git.fetch'] = () => status({ behind: 1, ahead: 1 });

    await store.push();

    expect(store.error()?.code).toBe('git.push_rejected');
    expect(store.status()?.behind).toBe(1);
    expect(store.busy()).toBeNull();
  });

  it('counts unsaved tabs, and saves them all', async () => {
    workspaceStore.documents.set([
      { id: 'a.yaml', key: 'a', path: 'a.yaml', dirty: true, saving: false } as never,
      { id: 'b.yaml', key: 'b', path: 'b.yaml', dirty: false, saving: false } as never,
    ]);
    expect(store.unsavedCount()).toBe(1);
    const save = vi.spyOn(workspaceStore, 'save').mockResolvedValue();

    await store.saveAll();

    expect(save).toHaveBeenCalledExactlyOnceWith('a.yaml');
  });

  it('loads the branches with the status', async () => {
    await vi.waitFor(() => expect(store.branches()).toEqual([main]));
  });

  it('switches branches and reloads the workspace from disk', async () => {
    handlers['git.branch.switch'] = () => ({
      status: status({ branch: 'feature' }),
      branches: [
        { ...main, current: false },
        { ...feature, current: true },
      ],
    });
    const refresh = vi.spyOn(workspaceStore, 'refresh').mockResolvedValue();
    const reload = vi.spyOn(workspaceStore, 'reloadFromDisk').mockResolvedValue();

    expect(await store.switchBranch('feature')).toBe(true);

    expect(send).toHaveBeenCalledWith('git.branch.switch', { name: 'feature' });
    expect(store.status()?.branch).toBe('feature');
    expect(store.branches()?.find((b) => b.current)?.name).toBe('feature');
    expect(store.notice()?.text).toBe('Switched to feature.');
    expect(refresh).toHaveBeenCalled();
    expect(reload).toHaveBeenCalled();
  });

  it('asks before switching with unsaved tabs', async () => {
    workspaceStore.documents.set([
      { id: 'a.yaml', key: 'a', path: 'a.yaml', dirty: true, saving: false } as never,
    ]);
    const confirm = vi.fn().mockReturnValue(false);
    vi.stubGlobal('confirm', confirm);

    expect(await store.switchBranch('feature')).toBe(false);

    expect(confirm.mock.calls[0][0]).toContain('1 open tab has unsaved changes');
    expect(send).not.toHaveBeenCalledWith('git.branch.switch', expect.anything());
    vi.unstubAllGlobals();
  });

  it('reports a switch refused over uncommitted changes', async () => {
    handlers['git.branch.switch'] = () => {
      throw new BridgeError('git.uncommitted_changes', 'You have uncommitted changes to: a.yaml.');
    };

    expect(await store.switchBranch('feature')).toBe(false);

    expect(store.failedOperation()).toBe('branch');
    expect(store.error()?.message).toContain('a.yaml');
  });
});
