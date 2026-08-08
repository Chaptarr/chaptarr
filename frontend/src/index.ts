import './polyfills';
import 'Styles/globals.css';
import './index.css';

const BOOTSTRAP_RECOVERY_KEY = 'chaptarr_bootstrap_recovery_ts';
const BOOTSTRAP_RECOVERY_WINDOW_MS = 60 * 1000;

function clearBootstrapRecoveryFlag() {
  try {
    sessionStorage.removeItem(BOOTSTRAP_RECOVERY_KEY);
  } catch {
    // ignore
  }
}

function tryAutomaticBootstrapRecovery() {
  try {
    const lastAttempt = Number(sessionStorage.getItem(BOOTSTRAP_RECOVERY_KEY));
    const now = Date.now();

    if (
      Number.isFinite(lastAttempt) &&
      now - lastAttempt < BOOTSTRAP_RECOVERY_WINDOW_MS
    ) {
      return false;
    }

    sessionStorage.setItem(BOOTSTRAP_RECOVERY_KEY, String(now));
  } catch {
    return false;
  }

  window.location.reload();
  return true;
}

function isWebpackLoadError(error) {
  const message =
    error && typeof error.message === 'string' ? error.message : '';
  const name = error && typeof error.name === 'string' ? error.name : '';

  return (
    name === 'ChunkLoadError' ||
    message.includes('ChunkLoadError') ||
    message.includes('Failed to fetch dynamically imported module') ||
    message.includes('Importing a module script failed') ||
    message.includes('Loading chunk') ||
    message.includes('Loading CSS chunk') ||
    message.includes('webpack_modules') ||
    message.includes('moduleId')
  );
}

function showRefreshRequiredMessage(error) {
  document.body.innerHTML = '';

  const container = document.createElement('div');
  container.style.cssText = `
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100vh;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
    background-color: #f8f9fa;
    color: #333;
    text-align: center;
    padding: 20px;
  `;

  const title = document.createElement('h1');
  title.textContent = 'Refresh Required';
  title.style.cssText = 'margin-bottom: 16px; color: #e67e22;';

  const message = document.createElement('p');
  message.textContent =
    'Chaptarr couldn’t load one or more application files. Please refresh the page to continue.';
  message.style.cssText =
    'margin-bottom: 24px; font-size: 16px; line-height: 1.5; max-width: 500px;';

  const details = document.createElement('p');
  details.textContent =
    'If this keeps happening behind a reverse proxy, disable caching for the UI and check for upstream timeouts.';
  details.style.cssText =
    'margin-bottom: 24px; font-size: 14px; line-height: 1.5; max-width: 500px; color: #555;';

  const errorMessage =
    error && typeof error.message === 'string' ? error.message : '';
  let technical = null;
  if (errorMessage) {
    technical = document.createElement('pre');
    technical.textContent = errorMessage;
    technical.style.cssText =
      'margin: 0 0 24px; padding: 12px; font-size: 12px; background: #fff; border: 1px solid #ddd; border-radius: 4px; max-width: 700px; overflow: auto;';
  }

  const button = document.createElement('button');
  button.textContent = 'Refresh Page';
  button.style.cssText = `
    background-color: #3498db;
    color: white;
    border: none;
    padding: 12px 24px;
    font-size: 16px;
    border-radius: 4px;
    cursor: pointer;
    transition: background-color 0.2s;
  `;
  button.onmouseover = () => (button.style.backgroundColor = '#2980b9');
  button.onmouseout = () => (button.style.backgroundColor = '#3498db');
  button.onclick = () => {
    try {
      const url = new URL(window.location.href);
      url.searchParams.set('cb', Date.now().toString());
      window.location.href = url.toString();
    } catch {
      window.location.reload();
    }
  };

  container.appendChild(title);
  container.appendChild(message);
  container.appendChild(details);
  if (technical) {
    container.appendChild(technical);
  }
  container.appendChild(button);
  document.body.appendChild(container);
}

async function start() {
  const initializeUrl = `${
    window.Chaptarr.urlBase
  }/initialize.json?t=${Date.now()}`;
  const response = await fetch(initializeUrl);

  window.Chaptarr = await response.json();

  /* eslint-disable no-undef, @typescript-eslint/ban-ts-comment */
  // @ts-ignore 2304
  __webpack_public_path__ = `${window.Chaptarr.urlBase}/`;
  /* eslint-enable no-undef, @typescript-eslint/ban-ts-comment */

  try {
    const { bootstrap } = await import('./bootstrap');
    await bootstrap();
    clearBootstrapRecoveryFlag();
  } catch (error) {
    if (isWebpackLoadError(error) && tryAutomaticBootstrapRecovery()) {
      return;
    }

    if (isWebpackLoadError(error)) {
      showRefreshRequiredMessage(error);
      return;
    }

    throw error;
  }
}

await start();
