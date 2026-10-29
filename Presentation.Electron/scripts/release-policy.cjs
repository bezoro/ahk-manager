const commitTypes = [
  ['feat', 'Funcionalidades'],
  ['fix', 'Correções'],
  ['perf', 'Desempenho'],
  ['refactor', 'Refatoração'],
  ['docs', 'Documentação'],
  ['ci', 'Integração contínua'],
  ['build', 'Build'],
  ['style', 'Formatação'],
  ['test', 'Testes'],
  ['chore', 'Manutenção'],
  ['revert', 'Reversões'],
].map(([type, section]) => ({ type, section, effect: 'bump' }));

module.exports = { commitTypes };
