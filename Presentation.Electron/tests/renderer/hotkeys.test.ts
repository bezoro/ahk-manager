import assert from 'node:assert/strict';
import test from 'node:test';
import { formatHotkey } from '../../src/renderer/hotkeys.js';

test('ordinary symbols become readable labels with stable modifier order', () => {
  for (const chord of ['!^#+u', '+#!^u', '^!+#u']) {
    assert.equal(formatHotkey({ chord }).label, 'Ctrl + Alt + Shift + Win + U');
  }
  assert.equal(formatHotkey({ chord: '^a' }).instruction, 'Pressione Ctrl + A.');
  for (const key of ['F1', 'F13', 'F24', 'f13']) {
    assert.equal(formatHotkey({ chord: key }).label, key.toUpperCase());
  }
});

test('sided modifiers and AltGr preserve physical modifier identity', () => {
  assert.equal(formatHotkey({ chord: '>!<^>+<#a' }).label, 'Ctrl esquerdo + Alt direito + Shift direito + Win esquerdo + A');
  assert.equal(formatHotkey({ chord: '<^>!a' }).label, 'AltGr (Ctrl esquerdo + Alt direito) + A');
  assert.equal(formatHotkey({ chord: '+<^>!a' }).label, 'AltGr (Ctrl esquerdo + Alt direito) + Shift + A');
  assert.equal(formatHotkey({ chord: '<^>^a' }).label, 'Ctrl esquerdo + Ctrl direito + A');
});

test('terminal symbols and punctuation are keys rather than discarded prefixes', () => {
  for (const key of ['+', '-', '!', '^', '#', '*', '~', '$', ';', ':', '"', "'", '/', '\\', ',', '.', '[', ']', '{', '}', '<', '>', '&', '=', '?', 'ç']) {
    assert.equal(formatHotkey({ chord: key }).label, key.toUpperCase());
    assert.equal(formatHotkey({ chord: '^' + key }).label, 'Ctrl + ' + key.toUpperCase());
  }
  assert.equal(formatHotkey({ chord: '++' }).label, 'Shift + +');
  assert.equal(formatHotkey({ chord: '<+>' }).label, 'Shift esquerdo + >');
});

test('named aliases and numpad keys use the shared vocabulary', () => {
  for (const [chord, expected] of [['RETURN', 'Enter'], ['Esc', 'Esc'], ['Del', 'Delete'], ['Space', 'Espaço'],
    ['PgDn', 'Page Down'], ['Numpad0', '0 (teclado numérico)'], ['NumpadAdd', '+ (teclado numérico)'],
    ['Volume_Mute', 'Silenciar áudio'], ['Browser_Back', 'Navegador: voltar']]) {
    assert.equal(formatHotkey({ chord: chord! }).label, expected);
  }
});

test('arrow Up differs from releasing a key or mouse button', () => {
  assert.equal(formatHotkey({ chord: 'Up' }).label, 'Seta para cima');
  assert.equal(formatHotkey({ chord: 'Up Up' }).instruction, 'Solte Seta para cima.');
  assert.equal(formatHotkey({ chord: '~LButton Up' }).label, 'Botão principal do mouse (ao soltar)');
  assert.equal(formatHotkey({ chord: '~LButton Up' }).instruction, 'Solte Botão principal do mouse.');
  assert.equal(formatHotkey({ chord: '^a Up' }).instruction, 'Mantenha Ctrl pressionado e solte A.');
  assert.ok(!formatHotkey({ chord: '~LButton Up' }).label.includes('duplo'));
});

test('mouse buttons and all wheel directions are readable', () => {
  for (const [chord, expected] of [['LButton', 'Botão principal do mouse'], ['RButton', 'Botão secundário do mouse'],
    ['MButton', 'Botão do meio do mouse'], ['XButton1', 'Botão adicional 1 do mouse'], ['XButton2', 'Botão adicional 2 do mouse'],
    ['WheelUp', 'Roda do mouse para cima'], ['WheelDown', 'Roda do mouse para baixo'],
    ['WheelLeft', 'Roda do mouse para a esquerda'], ['WheelRight', 'Roda do mouse para a direita']]) {
    assert.equal(formatHotkey({ chord: chord! }).label, expected);
  }
  assert.ok(formatHotkey({ chord: 'LButton' }).details.some(detail => detail.includes('lado físico')));
});

test('prefix semantics remain reachable in explanatory details', () => {
  const result = formatHotkey({ chord: '*~$^a' });
  assert.equal(result.label, 'Ctrl + A');
  for (const term of ['Curinga', 'Passagem', 'Hook']) assert.ok(result.details.some(detail => detail.includes(term)));
});

test('custom combinations instruct holding the first key and acting on the second', () => {
  assert.equal(formatHotkey({ chord: 'a & b' }).instruction, 'Mantenha A pressionado e pressione B.');
  assert.equal(formatHotkey({ chord: 'b & a' }).instruction, 'Mantenha B pressionado e pressione A.');
  assert.equal(formatHotkey({ chord: '~Space & LButton Up' }).instruction, 'Mantenha Espaço pressionado e solte Botão principal do mouse.');
  assert.equal(formatHotkey({ chord: 'a & b Up' }).label, 'A → B (ao soltar)');
});

test('declared double-click is generic, keeps modifiers, and states provenance', () => {
  for (const chord of ['LButton', 'RButton', 'MButton', 'XButton1', 'XButton2', '~^LButton Up']) {
    const result = formatHotkey({ chord, declaredGesture: 'DoubleClick' });
    assert.ok(result.label.includes('Clique duplo'));
    assert.ok(result.instruction.startsWith('Clique duas vezes'));
    assert.ok(result.details.some(detail => detail.includes('Gesto informado pelo script')));
    assert.ok(result.details.includes('Sintaxe AHK: ' + chord));
  }
  const modified = formatHotkey({ chord: '~^LButton Up', declaredGesture: 'DoubleClick' });
  assert.ok(modified.label.startsWith('Ctrl + '));
  assert.ok(modified.instruction.includes('mantendo Ctrl pressionado'));
  assert.ok(modified.details.some(detail => detail.includes('ao soltar')));
});

test('unsupported gesture declarations degrade to ordinary syntax defensively', () => {
  for (const chord of ['F13', 'WheelUp', 'a & LButton', 'UnknownThing']) {
    const result = formatHotkey({ chord, declaredGesture: 'DoubleClick' });
    assert.ok(!result.label.includes('Clique duplo'));
    assert.ok(result.details.some(detail => detail.includes('não se aplica')));
  }
  assert.deepEqual(formatHotkey({ chord: 'F13' }), formatHotkey({ chord: 'F13', declaredGesture: null }));
});

test('manual text chords remain supported without modifying stored strings', () => {
  assert.equal(formatHotkey({ chord: 'Alt + Ctrl + f13' }).label, 'Ctrl + Alt + F13');
  assert.equal(formatHotkey({ chord: 'ctrl+shift+Up' }).label, 'Ctrl + Shift + Seta para cima');
  assert.equal(formatHotkey({ chord: 'LCtrl + RAlt + x' }).label, 'Ctrl esquerdo + Alt direito + X');
});

test('VK and SC codes are preserved without inventing a physical key', () => {
  for (const key of ['vk41', 'SC01E', 'vkFFsc123']) {
    const result = formatHotkey({ chord: '^' + key });
    assert.equal(result.label, 'Ctrl + ' + key);
    assert.ok(result.details.some(detail => detail.includes('depende do teclado')));
  }
});

test('unknown and unsupported syntax stays visible with an honest explanation', () => {
  for (const chord of ['F25', 'SomeHelper()', 'vkZZ', 'sc12', 'Ctrl + Mystery', 'a & b & c', '^a & b',
    '^^a', '<^<^a', '~~a', '<^', 'a::Handler()', '<img src=x onerror=alert(1)>']) {
    const result = formatHotkey({ chord });
    assert.equal(result.label, chord);
    assert.ok(result.details.some(detail => detail.includes('não reconhecida')));
    assert.ok(result.details.includes('Sintaxe AHK: ' + chord));
  }
});

test('raw identity and caller metadata are untouched, including surrounding whitespace', () => {
  const input = Object.freeze({ chord: '  !^#+u  ', declaredGesture: null, normalizedChord: '!^#+U', hasConflict: true });
  const before = { ...input };
  const result = formatHotkey(input);
  assert.equal(result.label, 'Ctrl + Alt + Shift + Win + U');
  assert.equal(result.details[0], 'Sintaxe AHK:   !^#+u  ');
  assert.deepEqual(input, before);
  assert.equal(formatHotkey({ chord: '' }).label, 'Atalho não informado');
});

test('wheel instructions describe scrolling, including held modifiers and combinations', () => {
  assert.deepEqual(['WheelUp', 'WheelDown', 'WheelLeft', 'WheelRight'].map(chord => formatHotkey({ chord }).instruction),
    ['Role a roda do mouse para cima.', 'Role a roda do mouse para baixo.', 'Role a roda do mouse para a esquerda.', 'Role a roda do mouse para a direita.']);
  assert.equal(formatHotkey({ chord: '^WheelUp' }).instruction, 'Mantenha Ctrl pressionado e role a roda do mouse para cima.');
  assert.equal(formatHotkey({ chord: 'Space & WheelDown' }).instruction, 'Mantenha Espaço pressionado e role a roda do mouse para baixo.');
});

test('inherited dictionary properties remain unknown string labels', () => {
  const chords = ['constructor', '__proto__', 'toString'];
  assert.deepEqual(chords.map(chord => formatHotkey({ chord }).label), chords);
  for (const chord of chords) {
    const result = formatHotkey({ chord });
    assert.equal(typeof result.label, 'string');
    assert.ok(result.details.some(detail => detail.includes('não reconhecida')));
  }
});

test('neutral modifier instructions qualify release behavior without assuming counterpart declarations', () => {
  assert.deepEqual(['Ctrl', 'Control', 'Alt', 'Shift'].map(chord => formatHotkey({ chord }).instruction),
    ['Pressione e solte Ctrl.', 'Pressione e solte Ctrl.', 'Pressione e solte Alt.', 'Pressione e solte Shift.']);
  for (const chord of ['Ctrl', 'Control', 'Alt', 'Shift']) {
    const result = formatHotkey({ chord });
    assert.ok(result.details.some(detail => detail.includes('Up correspondente') && detail.includes('~')));
  }
  for (const chord of ['LCtrl', 'LControl', 'RCtrl', 'RControl', 'LAlt', 'RAlt', 'LShift', 'RShift', '~Ctrl']) {
    assert.ok(formatHotkey({ chord }).instruction.startsWith('Pressione '));
    assert.ok(!formatHotkey({ chord }).instruction.includes(' e solte '));
  }
  assert.equal(formatHotkey({ chord: 'Ctrl Up' }).instruction, 'Solte Ctrl.');
});

test('spaced manual Up-arrow chords do not become AHK release suffixes', () => {
  assert.deepEqual(['Ctrl + Up', 'Ctrl + Shift + Up'].map(chord => formatHotkey({ chord }).label),
    ['Ctrl + Seta para cima', 'Ctrl + Shift + Seta para cima']);
  assert.equal(formatHotkey({ chord: '^a Up' }).instruction, 'Mantenha Ctrl pressionado e solte A.');
});
