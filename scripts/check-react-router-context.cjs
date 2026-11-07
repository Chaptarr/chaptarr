'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const React = require('react');
const { renderToString } = require('react-dom/server');
const { Router } = require('react-router');
const { withRouter } = require('react-router-dom');
const { createMemoryHistory } = require('history');

const routerPath = fs.realpathSync(require.resolve('react-router'));
const domPath = require.resolve('react-router-dom');
const routerFromDomPath = fs.realpathSync(
  createRequire(domPath).resolve('react-router')
);

assert.equal(
  routerFromDomPath,
  routerPath,
  'react-router-dom and the Redux router must share one react-router installation'
);

const Probe = withRouter(({ location }) =>
  React.createElement('span', null, location.pathname)
);
const html = renderToString(
  React.createElement(
    Router,
    { history: createMemoryHistory({ initialEntries: ['/'] }) },
    React.createElement(Probe)
  )
);

assert.match(html, /<span[^>]*>\/<\/span>/);
console.log('React Router context is shared and withRouter renders inside Router.');
