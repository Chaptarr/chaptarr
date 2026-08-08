import { LOCATION_CHANGE } from 'connected-react-router';

const createNavigationMiddleware = () => {
  return (store) => (next) => (action) => {
    return next(action);
  };
};

export default createNavigationMiddleware;