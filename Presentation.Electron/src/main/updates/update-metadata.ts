export class UpdateRequestError extends Error {}
export const invalidUpdateMetadata = (): UpdateRequestError =>
  new UpdateRequestError(
    'The published release has invalid update metadata. No files were installed.',
  );

// JSON.parse already verifies grammar. This pass rejects duplicate object keys,
// including escaped spellings, before accepting security-relevant metadata.
export function parseUpdateJson(text: string): unknown {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw invalidUpdateMetadata();
  }
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
        if (scope.keys.has(key)) throw invalidUpdateMetadata();
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
