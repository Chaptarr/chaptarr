const loose = true;

module.exports = {
  plugins: [
    'lodash',
    '@babel/plugin-transform-logical-assignment-operators',

    // Stage 1
    '@babel/plugin-proposal-export-default-from',
    ['@babel/plugin-transform-optional-chaining', { loose }],
    ['@babel/plugin-transform-nullish-coalescing-operator', { loose }],

    // Stage 2
    '@babel/plugin-transform-export-namespace-from',

    // Stage 3
    ['@babel/plugin-transform-class-properties', { loose }],
    '@babel/plugin-syntax-dynamic-import'
  ],
  env: {
    development: {
      presets: [
        ['@babel/preset-react', { development: true }],
        '@babel/preset-typescript'
      ],
      plugins: [
        'babel-plugin-inline-classnames'
      ]
    },
    production: {
      presets: [
        ['@babel/env', {
          targets: {
            browsers: ['last 2 versions']
          },
          // The magic sauce
          modules: false,
          loose: true
        }],
        '@babel/preset-react',
        '@babel/preset-typescript'
      ],
      plugins: [
        'babel-plugin-transform-react-remove-prop-types'
      ]
    }
  }
};
