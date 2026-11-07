import serverMessageMatching from 'Utilities/Localization/server-message-match.cjs';
import serverMessages from 'Utilities/Localization/server-messages.json';
import { translateWithFallback } from 'Utilities/String/translate';

const { compileServerMessageMatchers, matchServerMessage } = serverMessageMatching;
const matchers = compileServerMessageMatchers(serverMessages);

// Only call this at a server-message presentation boundary. Unknown messages
// and captured user, library, provider, path, URL, and protocol data stay intact.
export default function translateServerMessage(message) {
  const match = matchServerMessage(message, matchers);

  if (!match) {
    return message;
  }

  // Isolate rendered values from the sentence direction. The matched API/history
  // string and each captured value stay intact; only this display result gains
  // first-strong isolate / pop directional isolate boundaries.
  const tokens = Object.fromEntries(Object.entries(match.tokens).map(([key, value]) => {
    const isIsolated = value.startsWith('\u2068') && value.endsWith('\u2069');

    return [key, !value || isIsolated ? value : `\u2068${value}\u2069`];
  }));

  return translateWithFallback(match.id, match.source, tokens);
}
