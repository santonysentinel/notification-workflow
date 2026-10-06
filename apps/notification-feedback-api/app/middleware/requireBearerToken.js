const defaultSkipPaths = ['/health', '/ping', '/favicon.ico'];

const normalizeSkipPaths = (paths = []) => {
  return paths
    .map((path) => (typeof path === 'string' ? path.trim() : path))
    .filter((path) => typeof path === 'string' && path.length > 0);
};

const shouldSkip = (req, skipList) => {
  if (!skipList.length) {
    return false;
  }
  const reqPath = req.path || req.originalUrl || '';
  return skipList.some((skipPath) => reqPath === skipPath || reqPath.startsWith(`${skipPath}/`));
};

const requireBearerToken = (options = {}) => {
  const skipList = normalizeSkipPaths([...(options.skipPaths || []), ...defaultSkipPaths]);
  const unauthorizedBody = options.unauthorizedBody || {
    error: 'Unauthorized. Bearer token required.'
  };

  return (req, res, next) => {
    if (shouldSkip(req, skipList)) {
      return next();
    }

    const authHeader = req.headers?.authorization;
    if (typeof authHeader === 'string') {
      const [scheme, token] = authHeader.split(' ');
      if (scheme?.toLowerCase() === 'bearer' && token && token.trim().length > 0) {
        return next();
      }
    }

    res.status(401).json(unauthorizedBody);
    return undefined;
  };
};

export default requireBearerToken;
