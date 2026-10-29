import assert from 'node:assert/strict';
import test from 'node:test';
import { formatHotkey } from '../../src/renderer/hotkeys.js';

test('ordinary symbols become readable labels with stable modifier order', () => {
  for (const chord of ['!^#+u', '+#!^u', '^!+#u']) {
    assert.equal(formatHotkey({ chord }).label, 'Ctrl + Alt + Shift + Win + U');
  }
  assert.equal(formatHotkey({ chord: '^a' }).instruction, 'Press Ctrl + A.');
  for (const key of ['F1', 'F13', 'F24', 'f13']) {
    assert.equal(formatHotkey({ chord: key }).label, key.toUpperCase());
  }
});

test('sided modifiers and AltGr preserve physical modifier identity', () => {
  assert.equal(
    formatHotkey({ chord: '>!<^>+<#a' }).label,
    'left Ctrl + right Alt + right Shift + left Win + A',
  );
  assert.equal(formatHotkey({ chord: '<^>!a' }).label, 'AltGr (left Ctrl + right Alt) + A');
  assert.equal(
    formatHotkey({ chord: '+<^>!a' }).label,
    'AltGr (left Ctrl + right Alt) + Shift + A',
  );
  assert.equal(formatHotkey({ chord: '<^>^a' }).label, 'left Ctrl + right Ctrl + A');
});

test('terminal symbols and punctuation are keys rather than discarded prefixes', () => {
  for (const key of [
    '+',
    '-',
    '!',
    '^',
    '#',
    '*',
    '~',
    '$',
    ';',
    ':',
    '"',
    "'",
    '/',
    '\\',
    ',',
    '.',
    '[',
    ']',
    '{',
    '}',
    '<',
    '>',
    '&',
    '=',
    '?',
    'ç',
  ]) {
    assert.equal(formatHotkey({ chord: key }).label, key.toUpperCase());
    assert.equal(formatHotkey({ chord: '^' + key }).label, 'Ctrl + ' + key.toUpperCase());
  }
  assert.equal(formatHotkey({ chord: '++' }).label, 'Shift + +');
  assert.equal(formatHotkey({ chord: '<+>' }).label, 'left Shift + >');
});

test('named aliases and numpad keys use the shared vocabulary', () => {
  for (const [chord, expected] of [
    ['RETURN', 'Enter'],
    ['Esc', 'Esc'],
    ['Del', 'Delete'],
    ['Space', 'Space'],
    ['PgDn', 'Page Down'],
    ['Numpad0', '0 (numpad)'],
    ['NumpadAdd', '+ (numpad)'],
    ['Volume_Mute', 'Mute audio'],
    ['Browser_Back', 'Browser: back'],
  ]) {
    assert.equal(formatHotkey({ chord: chord! }).label, expected);
  }
});

test('arrow Up differs from releasing a key or mouse button', () => {
  assert.equal(formatHotkey({ chord: 'Up' }).label, 'Up arrow');
  assert.equal(formatHotkey({ chord: 'Up Up' }).instruction, 'Release Up arrow.');
  assert.equal(formatHotkey({ chord: '~LButton Up' }).label, 'Primary mouse button (on release)');
  assert.equal(formatHotkey({ chord: '~LButton Up' }).instruction, 'Release Primary mouse button.');
  assert.equal(formatHotkey({ chord: '^a Up' }).instruction, 'Hold Ctrl and release A.');
  assert.ok(!formatHotkey({ chord: '~LButton Up' }).label.includes('Double-click'));
});

test('mouse buttons and all wheel directions are readable', () => {
  for (const [chord, expected] of [
    ['LButton', 'Primary mouse button'],
    ['RButton', 'Secondary mouse button'],
    ['MButton', 'Middle mouse button'],
    ['XButton1', 'Extra mouse button 1'],
    ['XButton2', 'Extra mouse button 2'],
    ['WheelUp', 'Mouse wheel up'],
    ['WheelDown', 'Mouse wheel down'],
    ['WheelLeft', 'Mouse wheel left'],
    ['WheelRight', 'Mouse wheel right'],
  ]) {
    assert.equal(formatHotkey({ chord: chord! }).label, expected);
  }
  assert.ok(
    formatHotkey({ chord: 'LButton' }).details.some((detail) => detail.includes('physical side')),
  );
});

test('prefix semantics remain reachable in explanatory details', () => {
  const result = formatHotkey({ chord: '*~$^a' });
  assert.equal(result.label, 'Ctrl + A');
  for (const term of ['Wildcard', 'Pass-through', 'Hook'])
    assert.ok(result.details.some((detail) => detail.includes(term)));
});

test('custom combinations instruct holding the first key and acting on the second', () => {
  assert.equal(formatHotkey({ chord: 'a & b' }).instruction, 'Hold A and press B.');
  assert.equal(formatHotkey({ chord: 'b & a' }).instruction, 'Hold B and press A.');
  assert.equal(
    formatHotkey({ chord: '~Space & LButton Up' }).instruction,
    'Hold Space and release Primary mouse button.',
  );
  assert.equal(formatHotkey({ chord: 'a & b Up' }).label, 'A → B (on release)');
});

test('declared double-click is generic, keeps modifiers, and states provenance', () => {
  for (const chord of ['LButton', 'RButton', 'MButton', 'XButton1', 'XButton2', '~^LButton Up']) {
    const result = formatHotkey({ chord, declaredGesture: 'DoubleClick' });
    assert.ok(result.label.includes('Double-click'));
    assert.ok(result.instruction.startsWith('Double-click the'));
    assert.ok(result.details.some((detail) => detail.includes('Script-declared gesture')));
    assert.ok(result.details.includes('AHK syntax: ' + chord));
  }
  const modified = formatHotkey({ chord: '~^LButton Up', declaredGesture: 'DoubleClick' });
  assert.ok(modified.label.startsWith('Ctrl + '));
  assert.ok(modified.instruction.includes('while holding Ctrl'));
  assert.ok(modified.details.some((detail) => detail.includes('released')));
});

test('unsupported gesture declarations degrade to ordinary syntax defensively', () => {
  for (const chord of ['F13', 'WheelUp', 'a & LButton', 'UnknownThing']) {
    const result = formatHotkey({ chord, declaredGesture: 'DoubleClick' });
    assert.ok(!result.label.includes('Double-click'));
    assert.ok(result.details.some((detail) => detail.includes('does not apply')));
  }
  assert.deepEqual(
    formatHotkey({ chord: 'F13' }),
    formatHotkey({ chord: 'F13', declaredGesture: null }),
  );
});

test('manual text chords remain supported without modifying stored strings', () => {
  assert.equal(formatHotkey({ chord: 'Alt + Ctrl + f13' }).label, 'Ctrl + Alt + F13');
  assert.equal(formatHotkey({ chord: 'ctrl+shift+Up' }).label, 'Ctrl + Shift + Up arrow');
  assert.equal(formatHotkey({ chord: 'LCtrl + RAlt + x' }).label, 'left Ctrl + right Alt + X');
});

test('VK and SC codes are preserved without inventing a physical key', () => {
  for (const key of ['vk41', 'SC01E', 'vkFFsc123']) {
    const result = formatHotkey({ chord: '^' + key });
    assert.equal(result.label, 'Ctrl + ' + key);
    assert.ok(result.details.some((detail) => detail.includes('depends on the keyboard')));
  }
});

test('unknown and unsupported syntax stays visible with an honest explanation', () => {
  for (const chord of [
    'F25',
    'SomeHelper()',
    'vkZZ',
    'sc12',
    'Ctrl + Mystery',
    'a & b & c',
    '^a & b',
    '^^a',
    '<^<^a',
    '~~a',
    '<^',
    'a::Handler()',
    '<img src=x onerror=alert(1)>',
  ]) {
    const result = formatHotkey({ chord });
    assert.equal(result.label, chord);
    assert.ok(result.details.some((detail) => detail.includes('not fully recognized')));
    assert.ok(result.details.includes('AHK syntax: ' + chord));
  }
});

test('raw identity and caller metadata are untouched, including surrounding whitespace', () => {
  const input = Object.freeze({
    chord: '  !^#+u  ',
    declaredGesture: null,
    normalizedChord: '!^#+U',
    hasConflict: true,
  });
  const before = { ...input };
  const result = formatHotkey(input);
  assert.equal(result.label, 'Ctrl + Alt + Shift + Win + U');
  assert.equal(result.details[0], 'AHK syntax:   !^#+u  ');
  assert.deepEqual(input, before);
  assert.equal(formatHotkey({ chord: '' }).label, 'Hotkey not specified');
});

test('wheel instructions describe scrolling, including held modifiers and combinations', () => {
  assert.deepEqual(
    ['WheelUp', 'WheelDown', 'WheelLeft', 'WheelRight'].map(
      (chord) => formatHotkey({ chord }).instruction,
    ),
    [
      'Scroll the mouse wheel up.',
      'Scroll the mouse wheel down.',
      'Scroll the mouse wheel left.',
      'Scroll the mouse wheel right.',
    ],
  );
  assert.equal(
    formatHotkey({ chord: '^WheelUp' }).instruction,
    'Hold Ctrl and scroll the mouse wheel up.',
  );
  assert.equal(
    formatHotkey({ chord: 'Space & WheelDown' }).instruction,
    'Hold Space and scroll the mouse wheel down.',
  );
});

test('inherited dictionary properties remain unknown string labels', () => {
  const chords = ['constructor', '__proto__', 'toString'];
  assert.deepEqual(
    chords.map((chord) => formatHotkey({ chord }).label),
    chords,
  );
  for (const chord of chords) {
    const result = formatHotkey({ chord });
    assert.equal(typeof result.label, 'string');
    assert.ok(result.details.some((detail) => detail.includes('not fully recognized')));
  }
});

test('neutral modifier instructions qualify release behavior without assuming counterpart declarations', () => {
  assert.deepEqual(
    ['Ctrl', 'Control', 'Alt', 'Shift'].map((chord) => formatHotkey({ chord }).instruction),
    [
      'Press and release Ctrl.',
      'Press and release Ctrl.',
      'Press and release Alt.',
      'Press and release Shift.',
    ],
  );
  for (const chord of ['Ctrl', 'Control', 'Alt', 'Shift']) {
    const result = formatHotkey({ chord });
    assert.ok(
      result.details.some(
        (detail) => detail.includes('matching Up hotkey') && detail.includes('~'),
      ),
    );
  }
  for (const chord of [
    'LCtrl',
    'LControl',
    'RCtrl',
    'RControl',
    'LAlt',
    'RAlt',
    'LShift',
    'RShift',
    '~Ctrl',
  ]) {
    assert.ok(formatHotkey({ chord }).instruction.startsWith('Press '));
    assert.ok(!formatHotkey({ chord }).instruction.includes(' e release '));
  }
  assert.equal(formatHotkey({ chord: 'Ctrl Up' }).instruction, 'Release Ctrl.');
});

test('spaced manual Up-arrow chords do not become AHK release suffixes', () => {
  assert.deepEqual(
    ['Ctrl + Up', 'Ctrl + Shift + Up'].map((chord) => formatHotkey({ chord }).label),
    ['Ctrl + Up arrow', 'Ctrl + Shift + Up arrow'],
  );
  assert.equal(formatHotkey({ chord: '^a Up' }).instruction, 'Hold Ctrl and release A.');
});
