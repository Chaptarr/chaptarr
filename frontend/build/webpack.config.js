/* eslint-disable @typescript-eslint/no-var-requires */
const { BundleAnalyzerPlugin } = require('webpack-bundle-analyzer');
const path = require('path');
const webpack = require('webpack');
const FileManagerPlugin = require('filemanager-webpack-plugin');
const HtmlWebpackPlugin = require('html-webpack-plugin');
const LiveReloadPlugin = require('webpack-livereload-plugin');
const MiniCssExtractPlugin = require('mini-css-extract-plugin');
const TerserPlugin = require('terser-webpack-plugin');
const { InjectManifest } = require('workbox-webpack-plugin');

// Temporarily disabled due to ajv compatibility issue in Docker build
// const ForkTsCheckerWebpackPlugin = require('fork-ts-checker-webpack-plugin');

module.exports = (env) => {
  const uiFolder = 'UI';
  const frontendFolder = path.join(__dirname, '..');
  const srcFolder = path.join(frontendFolder, 'src');
  const isProduction = !!env.production;
  const isProfiling = isProduction && !!env.profile;
  const inlineWebWorkers = 'no-fallback';

  const distFolder = path.resolve(frontendFolder, '..', '_output', uiFolder);

  console.log('Source Folder:', srcFolder);
  console.log('Output Folder:', distFolder);
  console.log('isProduction:', isProduction);
  console.log('isProfiling:', isProfiling);

  const config = {
    mode: isProduction ? 'production' : 'development',
    devtool: isProduction ? false : 'eval-source-map',
    target: 'web',

    stats: {
      children: false
    },

    watchOptions: {
      ignored: /node_modules/
    },

    entry: {
      index: 'index.ts'
    },

    resolve: {
      extensions: [
        '.ts',
        '.tsx',
        '.js'
      ],
      modules: [
        srcFolder,
        path.join(srcFolder, 'Shims'),
        'node_modules'
      ],
      alias: {
        jquery: 'jquery/dist/jquery.min',
        'react-middle-truncate': 'react-middle-truncate/lib/react-middle-truncate'
      },
      fallback: {
        buffer: false,
        http: false,
        https: false,
        url: false,
        util: false,
        net: false
      }
    },

    output: {
      path: distFolder,
      publicPath: '/',
      filename: isProduction ? '[name]-[contenthash].js' : '[name].js',
      chunkFilename: isProduction ? '[name]-[contenthash].js' : '[name].js',
      sourceMapFilename: '[file].map'
    },

    optimization: {
      moduleIds: 'deterministic',
      chunkIds: isProduction ? 'deterministic' : 'named',
      runtimeChunk: 'single',
      splitChunks: {
        chunks: 'all',
        cacheGroups: {
          react: {
            test: /[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/,
            name: 'vendor-react',
            chunks: 'all',
            priority: 20
          },
          vendors: {
            test: /[\\/]node_modules[\\/]/,
            name: 'vendors',
            chunks: 'all',
            priority: 10
          },
          lodash: {
            test: /[\\/]node_modules[\\/](lodash|lodash-es)[\\/]/,
            chunks: 'all',
            priority: 15,
            reuseExistingChunk: true
          },
          asyncVendors: {
            test: /[\\/]node_modules[\\/]/,
            chunks: 'async',
            minSize: 20000,
            priority: -10,
            reuseExistingChunk: true
          },
          default: {
            chunks: 'async',
            minChunks: 2,
            priority: -20,
            reuseExistingChunk: true
          }
        }
      }
    },

    performance: {
      hints: false
    },

    experiments: {
      topLevelAwait: true
    },

    plugins: [
      new webpack.DefinePlugin({
        __DEV__: !isProduction,
        'process.env.NODE_ENV': isProduction ? JSON.stringify('production') : JSON.stringify('development')
      }),

      new webpack.IgnorePlugin({ resourceRegExp: /^tough-cookie$/ }),
      new webpack.IgnorePlugin({ resourceRegExp: /^psl$/ }),
      new webpack.IgnorePlugin({ resourceRegExp: /^fetch-cookie$/ }),

      process.env.ANALYZER === 'true' ? new BundleAnalyzerPlugin() : null,

      new MiniCssExtractPlugin({
        filename: isProduction ? 'Content/styles-[contenthash].css' : 'Content/styles.css',
        chunkFilename: isProduction ? 'Content/[id]-[chunkhash].css' : 'Content/[id].css'
      }),

      new HtmlWebpackPlugin({
        template: 'frontend/src/index.ejs',
        filename: 'index.html',
        publicPath: '/',
        inject: false,
        templateParameters: {
          // Add cache buster based on build time
          cacheBuster: Date.now()
        }
      }),

      new FileManagerPlugin({
        events: {
          onEnd: {
            copy: [
              // HTML
              {
                source: 'frontend/src/*.html',
                destination: distFolder
              },

              // Fonts
              {
                source: 'frontend/src/Content/Fonts/*.*',
                destination: path.join(distFolder, 'Content/Fonts')
              },

              // Icon Images
              {
                source: 'frontend/src/Content/Images/Icons/*.*',
                destination: path.join(distFolder, 'Content/Images/Icons')
              },

              // Images
              {
                source: 'frontend/src/Content/Images/*.*',
                destination: path.join(distFolder, 'Content/Images')
              },

              // Robots
              {
                source: 'frontend/src/Content/robots.txt',
                destination: path.join(distFolder, 'Content/robots.txt')
              }
            ]
          }
        }
      }),

      // Temporarily disabled due to ajv compatibility issue in Docker build
      // new ForkTsCheckerWebpackPlugin({
      //   typescript: {
      //     configFile: path.join(frontendFolder, 'tsconfig.json')
      //   }
      // }),

      new LiveReloadPlugin(),

      // Service worker — production only (conflicts with HMR/LiveReload in dev).
      // InjectManifest bundles frontend/src/sw.js and inlines the precache manifest
      // so the emitted sw.js can be served at ${urlBase}/sw.js.
      isProduction
        ? new InjectManifest({
            swSrc: path.join(srcFolder, 'sw.js'),
            swDest: 'sw.js',
            // Do NOT include large font/image copies that FileManagerPlugin handles
            // separately — only webpack-emitted JS/CSS chunks go into the manifest.
            exclude: [
              /\.(?:png|jpe?g|gif|svg|ico|woff2?|ttf|otf|eot)$/i,
              /robots\.txt$/,
            ],
          })
        : null
    ].filter(Boolean),

    resolveLoader: {
      modules: [
        'node_modules',
        'frontend/build/webpack/'
      ]
    },

    module: {
      rules: [
        {
          test: /\.worker\.js$/,
          use: {
            loader: 'worker-loader',
            options: {
              filename: '[name].js',
              inline: inlineWebWorkers
            }
          }
        },
        {
          test: [/\.jsx?$/, /\.tsx?$/],
          exclude: /(node_modules|JsLibraries)/,
          use: [
            {
              loader: 'babel-loader',
              options: {
                configFile: `${frontendFolder}/babel.config.js`,
                envName: isProduction ? 'production' : 'development'
              }
            }
          ]
        },

        // CSS Modules
        {
          test: /\.css$/,
          exclude: /(node_modules|globals.css)/,
          use: [
            { loader: MiniCssExtractPlugin.loader },
            { loader: 'css-modules-typescript-loader' },
            {
              loader: 'css-loader',
              options: {
                importLoaders: 1,
                modules: {
                  localIdentName: isProduction ? '[name]/[local]/[hash:base64:5]' : '[name]/[local]'
                }
              }
            },
            {
              loader: 'postcss-loader',
              options: {
                postcssOptions: {
                  config: 'frontend/postcss.config.js'
                }
              }
            }
          ]
        },

        // Global styles
        {
          test: /\.css$/,
          include: /(node_modules|globals.css)/,
          use: [
            'style-loader',
            {
              loader: 'css-loader'
            }
          ]
        },

        // Fonts
        {
          test: /\.woff(2)?(\?v=[0-9]\.[0-9]\.[0-9])?$/,
          use: [
            {
              loader: 'url-loader',
              options: {
                limit: 10240,
                mimetype: 'application/font-woff',
                emitFile: false,
                name: 'Content/Fonts/[name].[ext]'
              }
            }
          ]
        },

        {
          test: /\.(ttf|eot|eot?#iefix|svg)(\?v=[0-9]\.[0-9]\.[0-9])?$/,
          use: [
            {
              loader: 'file-loader',
              options: {
                emitFile: false,
                name: 'Content/Fonts/[name].[ext]'
              }
            }
          ]
        }
      ]
    }
  };

  if (isProfiling) {
    config.resolve.alias['react-dom$'] = 'react-dom/profiling';
    config.resolve.alias['scheduler/tracing'] = 'scheduler/tracing-profiling';

    config.optimization = {
      minimize: true,
      minimizer: [
        new TerserPlugin({
          terserOptions: {
            sourceMap: true, // Must be set to true if using source-maps in production
            mangle: false,
            keep_classnames: true,
            keep_fnames: true
          }
        })
      ]
    };
  }

  return config;
};
