export default function createSentryMiddleware() {
  return (store) => (next) => (action) => next(action);
}
