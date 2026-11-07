import { RequestDto } from './messages';
import { EMPTY_AUTH, fromNodeDto, fromRequestDto, toRequestDto } from './request-mapping';
import { RequestContent } from './workspace';

const content: RequestContent = {
  name: 'Create user',
  method: 'POST',
  url: 'https://{{host}}/users',
  params: [{ key: 'page', value: '1', enabled: true }],
  headers: [{ key: 'Accept', value: 'application/json', enabled: false }],
  auth: { ...EMPTY_AUTH, type: 'bearer', token: 't0k', username: 'kept while editing' },
  bodyType: 'json',
  body: '{"name":"Ada"}',
  description: '# Notes',
  captures: [{ variable: 'token', expression: '$.token' }],
};

describe('request mapping', () => {
  describe('toRequestDto', () => {
    it('sends only the fields of the chosen auth type', () => {
      expect(toRequestDto(content).auth).toEqual({ type: 'bearer', token: 't0k' });
      expect(
        toRequestDto({ ...content, auth: { ...content.auth, type: 'basic', password: 'p' } }).auth,
      ).toEqual({ type: 'basic', username: 'kept while editing', password: 'p' });
      expect(
        toRequestDto({
          ...content,
          auth: { ...content.auth, type: 'apiKey', key: 'X-Key', value: 'v', in: 'query' },
        }).auth,
      ).toEqual({ type: 'apiKey', key: 'X-Key', value: 'v', in: 'query' });
      expect(toRequestDto({ ...content, auth: EMPTY_AUTH }).auth).toEqual({ type: 'none' });
    });

    it('drops the body content when there is no body', () => {
      expect(toRequestDto({ ...content, bodyType: 'none' }).body).toEqual({
        type: 'none',
        content: '',
      });
    });

    it('copies the rows instead of sharing them', () => {
      const dto = toRequestDto(content);
      expect(dto.params).toEqual(content.params);
      expect(dto.params[0]).not.toBe(content.params[0]);
    });
  });

  describe('fromRequestDto', () => {
    it('round-trips a request', () => {
      const { auth, ...rest } = fromRequestDto(toRequestDto(content));
      const { auth: original, ...expected } = content;
      expect(rest).toEqual(expected);
      expect(auth).toEqual({ ...EMPTY_AUTH, type: 'bearer', token: original.token });
    });

    it('normalizes unknown or missing values', () => {
      const dto = {
        name: 'odd',
        method: 'patch',
        url: '',
        params: [],
        headers: [],
        auth: { type: 'oauth', in: 'cookie' },
        body: { type: 'graphql', content: 'x' },
      } as unknown as RequestDto;
      const result = fromRequestDto(dto);
      expect(result.method).toBe('PATCH');
      expect(result.auth).toEqual(EMPTY_AUTH);
      expect(result.bodyType).toBe('none');
      expect(result.description).toBe('');
      expect(result.captures).toEqual([]);
      expect(fromRequestDto({ ...dto, method: 'BREW' }).method).toBe('GET');
    });
  });

  describe('fromNodeDto', () => {
    it('maps the tree, giving methods only to requests', () => {
      expect(
        fromNodeDto({
          path: 'users',
          name: 'users',
          kind: 'folder',
          method: 'post',
          children: [
            { path: 'users/get.yaml', name: 'Get', kind: 'request', method: 'get' },
            { path: 'users/bad.yaml', name: 'Bad', kind: 'request', error: 'Invalid YAML' },
          ],
        }),
      ).toEqual({
        path: 'users',
        name: 'users',
        kind: 'folder',
        method: undefined,
        error: undefined,
        children: [
          {
            path: 'users/get.yaml',
            name: 'Get',
            kind: 'request',
            method: 'GET',
            error: undefined,
            children: undefined,
          },
          {
            path: 'users/bad.yaml',
            name: 'Bad',
            kind: 'request',
            method: undefined,
            error: 'Invalid YAML',
            children: undefined,
          },
        ],
      });
    });
  });
});
