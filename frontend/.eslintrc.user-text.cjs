const base = require('./.eslintrc.js');

// The existing language rule is the authority; full lint debt is handled separately.
module.exports = {
  root: true,
  parser: base.parser,
  // Pin the Babel env so an ambient BABEL_ENV/NODE_ENV cannot disable the JSX presets.
  parserOptions: { ...base.parserOptions, babelOptions: { ...base.parserOptions.babelOptions, envName: 'production' } },
  env: base.env,
  globals: base.globals,
  plugins: base.plugins,
  settings: base.settings,
  rules: { 'react/jsx-no-literals': base.rules['react/jsx-no-literals'] },
  overrides: base.overrides.filter((override) => override.parser).map(({ files, parser, parserOptions }) => ({
    files, parser, parserOptions
  }))
};
