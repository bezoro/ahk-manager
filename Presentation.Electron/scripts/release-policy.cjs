const commitTypes = [
  ['feat', 'Features'],
  ['fix', 'Fixes'],
  ['perf', 'Performance'],
  ['refactor', 'Refactoring'],
  ['docs', 'Documentation'],
  ['ci', 'Continuous integration'],
  ['build', 'Build'],
  ['style', 'Formatting'],
  ['test', 'Tests'],
  ['chore', 'Maintenance'],
  ['revert', 'Reverts'],
].map(([type, section]) => ({ type, section, effect: 'bump' }));

module.exports = { commitTypes };
