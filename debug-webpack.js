#!/usr/bin/env node

console.log('Debug webpack wrapper');
console.log('Arguments received:', process.argv);
console.log('Arguments passed to webpack-cli:', process.argv.slice(2));

// Now run the real webpack-cli
require('./node_modules/webpack-cli/bin/cli.js');