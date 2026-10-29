const { commitTypes } = require('./scripts/release-policy.cjs');

module.exports = {
  extends: ['@commitlint/config-conventional'],
  defaultIgnores: true,
  rules: {
    'type-enum': [2, 'always', commitTypes.map(({ type }) => type)],
  },
};
