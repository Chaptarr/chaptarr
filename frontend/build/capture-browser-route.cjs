// A request event fires before its route handler. Await the captured handler instead.
module.exports = (label = 'browser route', timeoutMs = 30000) => {
  const routes = [];
  const waiters = [];
  return {
    capture(route) {
      if (waiters.length) {
        const waiter = waiters.shift();
        clearTimeout(waiter.timer);
        waiter.resolve(route);
      } else {
        routes.push(route);
      }
    },
    next() {
      if (routes.length) {
        return Promise.resolve(routes.shift());
      }
      return new Promise((resolve, reject) => {
        const waiter = { resolve };
        waiter.timer = setTimeout(() => {
          waiters.splice(waiters.indexOf(waiter), 1);
          reject(new Error(`Timed out after ${timeoutMs} ms waiting for ${label}`));
        }, timeoutMs);
        waiter.timer.unref();
        waiters.push(waiter);
      });
    }
  };
};
