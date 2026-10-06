import { v4 as uuidv4 } from 'uuid';
import { Logger as logger } from '../../configure/loggers.js';

function deriveContextValue(...sources) {
  for (const value of sources) {
    if (value && typeof value === 'string' && value.trim()) {
      return value.trim();
    }
  }
  return undefined;
}

export function requestContextMiddleware(options = {}) {
  const {
    userHeader = 'x-user-id'
  } = options;

  return (req, res, next) => {
    const startTime = Date.now();
    const traceId =
      deriveContextValue(req.headers['x-request-id'], req.uuid, req.headers['x-correlation-id']) ||
      uuidv4();

    // Ensure downstream consumers have access to the request identifier via both request and response headers.
    req.headers['x-request-id'] = traceId;
    res.setHeader('x-request-id', traceId);
    req.uuid = traceId;

    const initialContext = {
      traceId,
      userId: deriveContextValue(req.headers[userHeader], req.query?.userId, req.user?.id)
    };

    const buildLogger = (context = initialContext) =>
      logger.child({
        traceId: context.traceId,
        userId: context.userId || 'anonymous',
        method: req.method,
        path: req.originalUrl
      });

    req.context = { ...initialContext };
    let requestLogger = buildLogger(req.context);
    req.log = requestLogger;

    req.setContext = (patch = {}) => {
      const merged = {
        ...req.context,
        ...Object.fromEntries(
          Object.entries(patch)
            .filter(([, value]) => value !== undefined && value !== null && value !== '')
            .map(([key, value]) => [key, typeof value === 'string' ? value.trim() : value])
        )
      };
      req.context = merged;
      requestLogger = buildLogger(merged);
      req.log = requestLogger;
      return req.context;
    };

    res.on('finish', () => {
      requestLogger.info('request.completed', {
        statusCode: res.statusCode,
        durationMs: Date.now() - startTime,
        contentLength: res.get('Content-Length') || 0
      });
    });

    res.on('close', () => {
      if (!res.writableFinished) {
        requestLogger.warn('request.aborted', {
          statusCode: res.statusCode,
          durationMs: Date.now() - startTime
        });
      }
    });

    next();
  };
}

export default requestContextMiddleware;
