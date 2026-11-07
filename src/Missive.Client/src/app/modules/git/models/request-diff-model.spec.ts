import { EMPTY_AUTH } from '../../../models/request-mapping';
import { KeyValue, RequestContent } from '../../../models/workspace';
import {
  EMPTY_REQUEST,
  authFields,
  authValue,
  diffRows,
  requestChanges,
  textChange,
} from './request-diff-model';

const row = (key: string, value = '', enabled = true): KeyValue => ({ key, value, enabled });

describe('request diff model', () => {
  describe('textChange', () => {
    it('isolates the changed middle of two texts', () => {
      expect(textChange('/users/1', '/users/42')).toEqual({
        prefix: '/users/',
        removed: '1',
        added: '42',
        suffix: '',
      });
      expect(textChange('aXc', 'aYc')).toEqual({
        prefix: 'a',
        removed: 'X',
        added: 'Y',
        suffix: 'c',
      });
    });

    it('does not let prefix and suffix overlap on repeated characters', () => {
      expect(textChange('aa', 'aaa')).toEqual({
        prefix: 'aa',
        removed: '',
        added: 'a',
        suffix: '',
      });
    });

    it('handles identical texts', () => {
      expect(textChange('same', 'same')).toEqual({
        prefix: 'same',
        removed: '',
        added: '',
        suffix: '',
      });
    });
  });

  describe('diffRows', () => {
    it('keeps unchanged rows and reports additions and removals in order', () => {
      expect(diffRows([row('a'), row('b')], [row('a'), row('c'), row('b')])).toEqual([
        { kind: 'same', row: row('a') },
        { kind: 'added', row: row('c') },
        { kind: 'same', row: row('b') },
      ]);
      expect(diffRows([row('a'), row('b')], [row('b')])).toEqual([
        { kind: 'removed', row: row('a') },
        { kind: 'same', row: row('b') },
      ]);
    });

    it('pairs a removed and an added row with the same key as a change', () => {
      expect(diffRows([row('page', '1')], [row('page', '2')])).toEqual([
        { kind: 'changed', before: row('page', '1'), row: row('page', '2') },
      ]);
      expect(diffRows([row('page', '1')], [row('page', '1', false)])).toEqual([
        { kind: 'changed', before: row('page', '1'), row: row('page', '1', false) },
      ]);
    });

    it('handles empty tables', () => {
      expect(diffRows([], [])).toEqual([]);
      expect(diffRows([], [row('a')])).toEqual([{ kind: 'added', row: row('a') }]);
    });
  });

  describe('auth fields', () => {
    it('only reads fields the auth type uses', () => {
      const auth = { ...EMPTY_AUTH, type: 'bearer' as const, token: 't', username: 'u' };
      expect(authValue(auth, 'token')).toBe('t');
      expect(authValue(auth, 'username')).toBe('');
      expect(authValue(auth, 'type')).toBe('bearer');
    });

    it('lists the fields of both sides without repeats', () => {
      const basic = { ...EMPTY_AUTH, type: 'basic' as const };
      const bearer = { ...EMPTY_AUTH, type: 'bearer' as const };
      expect(authFields(basic, bearer).map((f) => f.field)).toEqual([
        'username',
        'password',
        'token',
      ]);
      expect(authFields(basic, basic).map((f) => f.field)).toEqual(['username', 'password']);
    });
  });

  describe('requestChanges', () => {
    const request: RequestContent = {
      ...EMPTY_REQUEST,
      name: 'Get user',
      url: '/users/1',
      headers: [row('Accept', 'application/json')],
    };

    it('reports nothing for equal requests', () => {
      expect(requestChanges(request, { ...request })).toEqual({
        name: false,
        line: false,
        params: 0,
        headers: 0,
        auth: false,
        body: false,
        docs: false,
      });
    });

    it('reports each changed section', () => {
      const changed = requestChanges(request, {
        ...request,
        method: 'POST',
        params: [row('a'), row('b')],
        headers: [],
        auth: { ...EMPTY_AUTH, type: 'bearer', token: 't' },
        bodyType: 'json',
        body: '{}',
        description: 'Now documented.',
      });
      expect(changed).toEqual({
        name: false,
        line: true,
        params: 2,
        headers: 1,
        auth: true,
        body: true,
        docs: true,
      });
    });

    it('ignores edits to fields the request does not use', () => {
      const noBody = { ...request, body: 'leftover' };
      const unusedAuth = { ...request, auth: { ...EMPTY_AUTH, token: 'unused' } };
      expect(requestChanges(request, noBody).body).toBe(false);
      expect(requestChanges(request, unusedAuth).auth).toBe(false);
    });
  });
});
