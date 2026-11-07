import { VariableLookup, describeVariable, splitVariables, variableClass } from './variables';

describe('variables', () => {
  const lookup: VariableLookup = new Map([
    ['host', { value: 'example.com', secret: false }],
    ['token', { value: 'abc', secret: true }],
  ]);

  describe('splitVariables', () => {
    it('splits plain text and references, allowing spaces inside the braces', () => {
      expect(splitVariables('https://{{host}}/users/{{ id }}')).toEqual([
        { text: 'https://' },
        { text: '{{host}}', variable: 'host' },
        { text: '/users/' },
        { text: '{{ id }}', variable: 'id' },
      ]);
    });

    it('returns a single plain segment without references', () => {
      expect(splitVariables('plain')).toEqual([{ text: 'plain' }]);
      expect(splitVariables('')).toEqual([]);
    });

    it('ignores incomplete or nested braces', () => {
      expect(splitVariables('{{a b}} {{}}')).toEqual([{ text: '{{a b}} {{}}' }]);
    });
  });

  describe('variableClass', () => {
    it('highlights every reference when the environment is unknown', () => {
      expect(variableClass('missing', null)).toBe('variable-highlight');
    });

    it('marks references the environment does not define', () => {
      expect(variableClass('host', lookup)).toBe('variable-highlight');
      expect(variableClass('missing', lookup)).toBe('variable-undefined');
    });
  });

  describe('describeVariable', () => {
    it('describes values, secrets and undefined references', () => {
      expect(describeVariable('host', null)).toBe('Variable host');
      expect(describeVariable('host', lookup)).toBe('host = example.com');
      expect(describeVariable('token', lookup)).toBe('token: secret value');
      const captured = new Map([['jwt', { value: 'x', secret: false, captured: true }]]);
      expect(describeVariable('jwt', captured)).toBe('jwt = x (captured)');
      expect(describeVariable('missing', lookup)).toBe(
        'missing is not defined in the selected environment',
      );
    });
  });
});
