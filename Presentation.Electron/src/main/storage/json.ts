/** JSON grammar plus duplicate keys, including escaped spellings. */
export function parseStorageJson(text: string): unknown {
  const parsed: unknown = JSON.parse(text);
  const stack: Array<{ object: boolean; key: boolean; keys: Set<string> }> = [];
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (character === '"') {
      const start = index++;
      for (; index < text.length; index++) {
        if (text[index] === '\\') index++;
        else if (text[index] === '"') break;
      }
      const scope = stack.at(-1);
      if (scope?.object && scope.key) {
        const key = JSON.parse(text.slice(start, index + 1)) as string;
        if (scope.keys.has(key)) throw new Error('Data settings contain duplicate fields.');
        scope.keys.add(key);
      }
    } else if (character === '{' || character === '[')
      stack.push({ object: character === '{', key: character === '{', keys: new Set() });
    else if (character === '}' || character === ']') stack.pop();
    else if (character === ':' && stack.at(-1)?.object) stack.at(-1)!.key = false;
    else if (character === ',' && stack.at(-1)?.object) stack.at(-1)!.key = true;
  }
  return parsed;
}

export function storageObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function storageKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  return (
    Object.keys(value).length === keys.length && keys.every((key) => Object.hasOwn(value, key))
  );
}
