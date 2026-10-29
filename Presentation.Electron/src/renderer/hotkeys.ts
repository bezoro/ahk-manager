import { hotkeyText as text } from './resources/hotkeys-en-US.js';

export interface ReadableHotkey {
  label: string;
  instruction: string;
  details: readonly string[];
}

type ModifierKind = keyof typeof text.modifiers;
type Modifier = { kind: ModifierKind; side?: 'left' | 'right' };
type Key = { label: string; token: string; coded: boolean };
const modifierSymbols: Readonly<Record<string, ModifierKind>> = {
  '^': 'ctrl',
  '!': 'alt',
  '+': 'shift',
  '#': 'win',
};
const modifierOrder: readonly ModifierKind[] = ['ctrl', 'alt', 'shift', 'win'];
const mouseButtons = new Set(['lbutton', 'rbutton', 'mbutton', 'xbutton1', 'xbutton2']);

function readKey(token: string): Key | null {
  const normalized = token.toLowerCase();
  const known = Object.hasOwn(text.keys, normalized) ? text.keys[normalized] : undefined;
  if (known) return { label: known, token: normalized, coded: false };
  if (/^f(?:[1-9]|1\d|2[0-4])$/i.test(token))
    return { label: token.toUpperCase(), token: normalized, coded: false };
  if (/^numpad\d$/i.test(token))
    return { label: `${token.slice(-1)} (numpad)`, token: normalized, coded: false };
  if (/^(?:vk[\da-f]{2}(?:sc[\da-f]{3})?|sc[\da-f]{3})$/i.test(token)) {
    return { label: token, token: normalized, coded: true };
  }
  if (Array.from(token).length === 1 && !/\s/u.test(token)) {
    return { label: token.toUpperCase(), token: normalized, coded: false };
  }
  return null;
}

function keyAction(key: Key, release: boolean): string {
  if (release) return `release ${key.label}`;
  return Object.hasOwn(text.wheelActions, key.token)
    ? text.wheelActions[key.token]!
    : `press ${key.label}`;
}

function modifierLabel(modifiers: readonly Modifier[]): string[] {
  return modifierOrder.flatMap((kind) =>
    modifiers
      .filter((item) => item.kind === kind)
      .map((item) => `${item.side ? `${text[item.side]} ` : ''}${text.modifiers[kind]}`),
  );
}

function manualModifiers(chord: string): { modifiers: Modifier[]; key: string } | null {
  const parts = chord.split(/\s*\+\s*/);
  if (parts.length < 2) return null;
  const modifiers: Modifier[] = [];
  for (const part of parts.slice(0, -1)) {
    const match = /^(l|r)?(ctrl|control|alt|shift|win)$/i.exec(part);
    if (!match) return null;
    const name = match[2]!.toLowerCase();
    modifiers.push({
      kind: name === 'control' ? 'ctrl' : (name as ModifierKind),
      ...(match[1]
        ? { side: match[1].toLowerCase() === 'l' ? ('left' as const) : ('right' as const) }
        : {}),
    });
  }
  return { modifiers, key: parts.at(-1)! };
}

/** Translate declaration notation only; never inspect a handler, filename, or script body. */
export function formatHotkey(input: {
  chord: string;
  declaredGesture?: 'DoubleClick' | null;
}): ReadableHotkey {
  const details = [text.raw + input.chord];
  let chord = input.chord.trim();
  const fallback = (): ReadableHotkey => {
    details.push(text.unknown);
    if (input.declaredGesture) details.push(text.invalidGesture);
    return {
      label: input.chord || text.missing,
      instruction: input.chord ? `AHK hotkey: ${input.chord}. ${text.unknown}` : text.missing,
      details,
    };
  };
  if (!chord) return fallback();

  const manualChord = manualModifiers(chord);
  const manualArrow = manualChord && readKey(manualChord.key);
  const release = !manualArrow && /\s+up$/i.test(chord);
  if (release) {
    chord = chord.replace(/\s+up$/i, '').trimEnd();
    details.push(text.release);
  }
  const modifiers: Modifier[] = [];
  const prefixes = new Set<string>();
  let altGr = false;
  // Leave the final character intact: +, !, ^, #, *, ~ and $ can themselves be keys.
  while (chord.length > 1) {
    const symbol = chord[0]!;
    if ('*~$'.includes(symbol)) {
      if (prefixes.has(symbol)) return fallback();
      prefixes.add(symbol);
      chord = chord.slice(1);
    } else if (chord.startsWith('<^>!') && chord.length > 4) {
      if (altGr) return fallback();
      altGr = true;
      modifiers.push({ kind: 'ctrl', side: 'left' }, { kind: 'alt', side: 'right' });
      chord = chord.slice(4);
    } else {
      const side = symbol === '<' ? 'left' : symbol === '>' ? 'right' : undefined;
      const modifier = modifierSymbols[chord[side ? 1 : 0]!];
      const consumed = side ? 2 : 1;
      if (!modifier || chord.length <= consumed) break;
      modifiers.push({ kind: modifier, ...(side ? { side } : {}) });
      chord = chord.slice(consumed);
    }
  }
  if (prefixes.has('*')) details.push(text.wildcard);
  if (prefixes.has('~')) details.push(text.passThrough);
  if (prefixes.has('$')) details.push(text.hook);

  if (!modifiers.length && !altGr) {
    const manual = manualModifiers(chord);
    if (manual) {
      modifiers.push(...manual.modifiers);
      chord = manual.key;
    }
  }
  // Repeated/general plus sided versions are ambiguous; preserve them verbatim.
  if (
    modifiers.some((item, index) =>
      modifiers
        .slice(0, index)
        .some(
          (previous) =>
            previous.kind === item.kind &&
            (!previous.side || !item.side || previous.side === item.side),
        ),
    )
  )
    return fallback();
  let modifierLabels = modifierLabel(modifiers);
  if (altGr)
    modifierLabels = [
      text.altGr,
      ...modifierLabel(
        modifiers.filter(
          (item) =>
            !(item.kind === 'ctrl' && item.side === 'left') &&
            !(item.kind === 'alt' && item.side === 'right'),
        ),
      ),
    ];

  const combination = chord.split(/\s+&\s+/);
  if (combination.length > 1) {
    if (combination.length !== 2 || modifiers.length) return fallback();
    const held = readKey(combination[0]!);
    const pressed = readKey(combination[1]!);
    if (!held || !pressed) return fallback();
    if (held.coded || pressed.coded) details.push(text.code);
    for (const key of [held, pressed]) {
      if (key.token === 'lbutton') details.push(text.primary);
      if (key.token === 'rbutton') details.push(text.secondary);
    }
    if (input.declaredGesture) details.push(text.invalidGesture);
    return {
      label: `${held.label} → ${pressed.label}${release ? ' (on release)' : ''}`,
      instruction: `Hold ${held.label} and ${keyAction(pressed, release)}.`,
      details,
    };
  }

  const key = readKey(chord);
  if (!key) return fallback();
  if (key.coded) details.push(text.code);
  if (key.token === 'lbutton') details.push(text.primary);
  if (key.token === 'rbutton') details.push(text.secondary);
  if (input.declaredGesture === 'DoubleClick' && mouseButtons.has(key.token)) {
    details.push(text.gesture);
    return {
      label: [...modifierLabels, `${text.doubleClick} (${key.label.toLowerCase()})`].join(' + '),
      instruction: `Double-click the ${key.label.toLowerCase()}${modifierLabels.length ? ` while holding ${modifierLabels.join(' + ')}` : ''}.`,
      details,
    };
  }
  if (input.declaredGesture) details.push(text.invalidGesture);
  const label = [...modifierLabels, key.label].join(' + ');
  if (
    !release &&
    !modifiers.length &&
    !prefixes.has('~') &&
    ['ctrl', 'control', 'alt', 'shift'].includes(key.token)
  ) {
    details.push(text.neutralModifier);
    return { label, instruction: `Press and release ${key.label}.`, details };
  }
  if (Object.hasOwn(text.wheelActions, key.token) && !release) {
    const action = keyAction(key, false);
    return {
      label,
      instruction: modifierLabels.length
        ? `Hold ${modifierLabels.join(' + ')} and ${action}.`
        : `${action[0]!.toUpperCase()}${action.slice(1)}.`,
      details,
    };
  }
  const instruction =
    release && modifierLabels.length
      ? `Hold ${modifierLabels.join(' + ')} and release ${key.label}.`
      : `${release ? 'Release' : 'Press'} ${label}.`;
  return { label: label + (release ? ' (on release)' : ''), instruction, details };
}
