import axios from 'axios';
import express from 'express';
import helmet from 'helmet';
import cors from 'cors';
import nconf from 'nconf';
import bodyParser from 'body-parser';
import morgan from 'morgan';
import { Errors } from './errors.js';
import configFile from './configuration.js';
import { Logger as logger, MorganAccessStream } from './loggers.js';
import routes from '../app/routes/routes.js';
import { setupDefaultMetricsConfig, initializeMetrics } from '../app/services/metricsService.js';
import {
  metricsMiddleware,
  businessMetricsMiddleware
} from '../app/middleware/metricsMiddleware.js';
import requestContextMiddleware from '../app/middleware/requestContextMiddleware.js';
import requireBearerToken from '../app/middleware/requireBearerToken.js';
import {
  registerDependency,
  recordDependencyAttempt,
  recordDependencySuccess,
  recordDependencyFailure,
  markAppReady,
  markAppNotReady,
  markAppShuttingDown,
  getHealthSnapshot,
  getLivenessSnapshot
} from './health.js';

const isTestEnv = process.env.NODE_ENV === 'test';

const DEFAULT_RETRY_CONFIG = {
  maxAttempts: 5,
  baseDelayMs: 500,
  maxDelayMs: 5000,
  backoffMultiplier: 2,
  jitterRatio: 0.2
};

const DEFAULT_HEALTH_CHECK_CONFIG = {
  path: '/health/ready',
  method: 'get',
  timeoutMs: 5000,
  expectedStatus: [200]
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const parseNumber = (value, fallback) => {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
};

function sanitizeEnvPrefix(prefix) {
  if (!prefix) return undefined;
  return String(prefix)
    .trim()
    .replace(/[^a-zA-Z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toUpperCase();
}

function buildRetryOptions(retryConfig, envPrefix, defaults = DEFAULT_RETRY_CONFIG) {
  const cfg = retryConfig || {};
  const prefix = sanitizeEnvPrefix(envPrefix);
  const readEnv = (key) => (prefix ? process.env[`${prefix}_${key}`] : undefined);

  return {
    maxAttempts: parseNumber(
      cfg.maxAttempts ?? readEnv('MAX_ATTEMPTS'),
      defaults.maxAttempts
    ),
    baseDelayMs: parseNumber(
      cfg.baseDelayMs ?? readEnv('BASE_DELAY_MS'),
      defaults.baseDelayMs
    ),
    maxDelayMs: parseNumber(
      cfg.maxDelayMs ?? readEnv('MAX_DELAY_MS'),
      defaults.maxDelayMs
    ),
    backoffMultiplier: parseNumber(
      cfg.backoffMultiplier ?? readEnv('BACKOFF_MULTIPLIER'),
      defaults.backoffMultiplier
    ),
    jitterRatio: parseNumber(
      cfg.jitterRatio ?? readEnv('JITTER_RATIO'),
      defaults.jitterRatio
    )
  };
}

function toArray(value) {
  if (Array.isArray(value)) {
    return value;
  }
  if (value === undefined || value === null) {
    return [];
  }
  return [value];
}

function normalizeMicroserviceDependencies(raw) {
  if (!raw) {
    return [];
  }

  if (Array.isArray(raw)) {
    return raw
      .map((entry, index) => {
        if (!entry || typeof entry !== 'object') {
          return null;
        }
        const name = entry.name || `microservice_${index + 1}`;
        return {
          name,
          baseUrl: (entry.baseUrl || entry.url || '').trim(),
          health: entry.health || {},
          retry: entry.retry || {},
          component: entry.component,
          type: entry.type,
          envPrefix: entry.envPrefix || name
        };
      })
      .filter((entry) => entry && entry.name && entry.baseUrl);
  }

  if (typeof raw === 'object') {
    return Object.entries(raw)
      .map(([name, value]) => {
        if (typeof value === 'string') {
          return {
            name,
            baseUrl: value.trim(),
            health: {},
            retry: {},
            component: 'microservice',
            type: 'http',
            envPrefix: name
          };
        }

        if (value && typeof value === 'object') {
          return {
            name,
            baseUrl: (value.baseUrl || value.url || value.host || '').trim(),
            health: value.health || {},
            retry: value.retry || {},
            component: value.component,
            type: value.type,
            envPrefix: value.envPrefix || name
          };
        }

        return null;
      })
      .filter((entry) => entry && entry.name && entry.baseUrl);
  }

  return [];
}

function buildHealthUrl(baseUrl, healthConfig = {}) {
  if (healthConfig.url) {
    return healthConfig.url;
  }

  const path = healthConfig.path ?? DEFAULT_HEALTH_CHECK_CONFIG.path;
  if (!path) {
    return baseUrl;
  }

  if (/^https?:\/\//i.test(path)) {
    return path;
  }

  try {
    const url = new URL(baseUrl);
    const normalizedBasePath = url.pathname.endsWith('/')
      ? url.pathname.slice(0, -1)
      : url.pathname;
    if (path.startsWith('/')) {
      url.pathname = `${normalizedBasePath}${path}`.replace(/\/+/g, '/');
    } else {
      const basePath = normalizedBasePath ? `${normalizedBasePath}/` : '/';
      url.pathname = `${basePath}${path}`.replace(/\/+/g, '/');
    }
    return url.toString();
  } catch (error) {
    const normalizedBase = baseUrl.endsWith('/') ? baseUrl.slice(0, -1) : baseUrl;
    const normalizedPath = path.startsWith('/') ? path : `/${path}`;
    return `${normalizedBase}${normalizedPath}`;
  }
}

async function verifyMicroserviceHealth(service) {
  const health = { ...DEFAULT_HEALTH_CHECK_CONFIG, ...(service.health || {}) };
  const timeoutMs = parseNumber(health.timeoutMs, DEFAULT_HEALTH_CHECK_CONFIG.timeoutMs);
  const method = String(health.method || DEFAULT_HEALTH_CHECK_CONFIG.method).toLowerCase();
  const url = buildHealthUrl(service.baseUrl, health);

  const response = await axios({
    method,
    url,
    timeout: timeoutMs,
    headers: health.headers,
    validateStatus: () => true
  });

  const expectedStatuses = (
    toArray(health.expectedStatus).length
      ? toArray(health.expectedStatus)
      : toArray(DEFAULT_HEALTH_CHECK_CONFIG.expectedStatus)
  )
    .map((status) => Number(status))
    .filter((status) => Number.isFinite(status));

  const acceptableStatuses = expectedStatuses.length
    ? expectedStatuses
    : toArray(DEFAULT_HEALTH_CHECK_CONFIG.expectedStatus);

  if (!acceptableStatuses.includes(response.status)) {
    const error = new Error(`Unexpected status ${response.status} from ${url}`);
    error.status = response.status;
    error.responseBody = response.data;
    error.healthCheckUrl = url;
    error.dependency = service.name;
    error.baseUrl = service.baseUrl;
    throw error;
  }

  return response.data;
}

async function initializeWithRetry(name, initFn, options = {}) {
  const retry = { ...DEFAULT_RETRY_CONFIG, ...options };
  const unlimited = retry.maxAttempts === 0 || retry.maxAttempts === Infinity;
  let attempt = 0;
  while (unlimited || attempt < retry.maxAttempts) {
    attempt += 1;
    recordDependencyAttempt(name);
    try {
      const result = await initFn();
      recordDependencySuccess(name);
      logger.info('[STARTUP] Dependency ready', { name, attempt });
      return { success: true, attempts: attempt, result };
    } catch (error) {
      recordDependencyFailure(name, error);
      const metadata = {
        name,
        attempt,
        maxAttempts: retry.maxAttempts,
        message: error?.message || String(error),
        status: error?.status,
        healthCheckUrl: error?.healthCheckUrl,
        baseUrl: error?.baseUrl
      };
      logger.error('[STARTUP] Dependency initialization failed', metadata);
      if (!unlimited && attempt >= retry.maxAttempts) {
        return { success: false, attempts: attempt, error };
      }
      const backoff = Math.min(
        retry.maxDelayMs,
        retry.baseDelayMs * Math.pow(retry.backoffMultiplier, attempt - 1)
      );
      const jitter = backoff * (retry.jitterRatio ?? 0);
      await sleep(backoff + Math.random() * jitter);
    }
  }

  return { success: false, attempts: attempt };
}

async function bootstrapDependencies() {
  const services = normalizeMicroserviceDependencies(nconf.get('dependencies:microservices'));

  services.forEach((service) => {
    registerDependency(service.name, {
      component: service.component || 'microservice',
      type: service.type || 'http',
      baseUrl: service.baseUrl,
      healthCheck: {
        url: service.health?.url,
        path: service.health?.path
      }
    });
  });

  if (services.length === 0) {
    markAppReady();
    return true;
  }

  if (isTestEnv && process.env.ENABLE_STARTUP_DEPENDENCY_INIT !== 'true') {
    services.forEach((svc) => recordDependencySuccess(svc.name));
    markAppReady();
    return true;
  }

  markAppNotReady();

  const results = await Promise.all(
    services.map((service) => {
      const retry = buildRetryOptions(service.retry, service.envPrefix || service.name);
      return initializeWithRetry(service.name, () => verifyMicroserviceHealth(service), retry);
    })
  );

  const allReady = results.every((result) => result.success);

  if (allReady) {
    markAppReady();
    logger.info('[STARTUP] All dependencies ready', {
      services: results.map((result, index) => ({
        name: services[index].name,
        attempts: result.attempts
      }))
    });
  } else {
    logger.warn('[STARTUP] Service running in degraded mode', {
      services: results.map((result, index) => ({
        name: services[index].name,
        success: result.success,
        attempts: result.attempts,
        error: result.error ? result.error.message : undefined
      }))
    });
  }

  return allReady;
}

// Load config
nconf.argv().env();

// Setup default metrics configuration (lowest precedence)
setupDefaultMetricsConfig();

nconf.file({
  file: `./${configFile}`
});

// Initialize metrics client when enabled
if (nconf.get('logging:enableMetrics') !== false) {
  initializeMetrics();
}

const dependenciesReady = await bootstrapDependencies();
if (!dependenciesReady) {
  logger.warn('[STARTUP] Not all dependencies became ready before server start');
}

const app = express();

// Determine allowed dev origins (adjust as needed)
const DEV_UI_ORIGIN = process.env.DEV_UI_ORIGIN || 'http://localhost:3001';

// Custom helmet config: relax CORP and COOP for multi-port local dev, allow images from any origin
app.use(
  helmet({
    crossOriginResourcePolicy: { policy: 'cross-origin' }, // allow embedding across localhost ports
    crossOriginOpenerPolicy: false, // disable COOP to avoid isolation blocking subresources
    contentSecurityPolicy: {
      useDefaults: true,
      directives: {
        // Extend default directives
        'default-src': ["'self'"],
        'img-src': ["'self'", 'data:', DEV_UI_ORIGIN, 'blob:', 'https:'],
        'script-src': ["'self'"],
        'style-src': ["'self'", 'https:', "'unsafe-inline'"],
        'font-src': ["'self'", 'https:', 'data:'],
        'object-src': ["'none'"],
        'frame-ancestors': ["'self'"],
        'upgrade-insecure-requests': []
      }
    }
  })
);

// CORS: allow UI dev origin & credentials if needed
app.use(
  cors({
    origin: (origin, cb) => {
      if (!origin) return cb(null, true); // same-origin/no-origin (curl, etc.)
      if (origin === DEV_UI_ORIGIN) return cb(null, true);
      return cb(null, true); // Broader allowance for now; tighten later if needed
    },
    credentials: false
  })
);

// Attach request context/logging before metrics
app.use(requestContextMiddleware());

// Metrics middleware (before other middleware for accurate timing)
app.use(
  metricsMiddleware({
    includeUserAgent: true,
    skipPaths: ['/health', '/health/ready', '/health/live', '/ping', '/favicon.ico']
  })
);

// Access logs via morgan
app.use(
  morgan(
    ':remote-addr [:date[clf]] ":method :url HTTP/:http-version" :status :res[content-length] :response-time ms ":user-agent"',
    { stream: MorganAccessStream }
  )
);

app.use(bodyParser.urlencoded({ extended: true, limit: '50mb' }));
app.use(bodyParser.json({ limit: '50mb' }));

// Refresh context with body/query/param data once parsers have run
app.use((req, _res, next) => {
  if (typeof req.setContext === 'function') {
    req.setContext({
      userId: req.body?.participantId || req.query?.participantId || req.user?.id
    });
  }
  next();
});

// Authorization token parser middleware
app.use((req, res, next) => {
  try {
    let token = '';
    if (req.headers?.authorization) {
      const [type, value] = req.headers.authorization.split(' ');
      if (type?.toLowerCase() === 'bearer') {
        token = value;
      }
    }
    req.token = token;
    next();
  } catch (ex) {
    next();
  }
});

app.use(
  requireBearerToken({
    skipPaths: ['/api/v1/public']
  })
);

// Business metrics middleware (after auth/token parsing)
app.use(businessMetricsMiddleware);

const skipApiRoutes = isTestEnv && process.env.SKIP_API_ROUTE_REGISTRATION === 'true';
if (!skipApiRoutes) {
  routes(app);
}

app.get('/health/live', (_req, res) => {
  const snapshot = getLivenessSnapshot();
  const statusCode = snapshot.status === 'alive' ? 200 : 503;
  res.status(statusCode).json(snapshot);
});

app.get('/health/ready', (_req, res) => {
  const snapshot = getHealthSnapshot();
  res.status(snapshot.ready ? 200 : 503).json(snapshot);
});

// 404 handler
app.use((req, res) => {
  res.status(404).json(Errors.FOUR_ZERO_FOUR);
});

// Error handler
app.use((err, req, res, next) => {
  res.status(err.status || 500).json({
    ...Errors.FIVE_ZERO_ZERO,
    status_code: err.status || 500,
    message: err.message || 'Internal Error'
  });
  (req.log || logger).error('request.unhandled_error', {
    message: err.message,
    stack: err.stack,
    traceId: req?.context?.traceId
  });
  next(err);
});

const port = nconf.get('server:port');
logger.info('[SERVER] Starting', { port });

const server = app.listen(port, () => {
  logger.info('[SERVER] Listening', { port });
});

if (!isTestEnv) {
  const shutdownSignals = ['SIGTERM', 'SIGINT'];
  let shuttingDown = false;

  const handleShutdown = async (signal) => {
    if (shuttingDown) return;
    shuttingDown = true;

    logger.info('[SERVER] Shutdown signal received', { signal });
    markAppShuttingDown();

    const closeServer = new Promise((resolve) => {
      server.close((err) => {
        if (err) {
          logger.error('[SERVER] Error while closing HTTP listener', {
            message: err.message,
            stack: err.stack
          });
        }
        resolve();
      });
    });

    await closeServer;

    process.exit(0);
  };

  shutdownSignals.forEach((signal) => {
    process.once(signal, () => {
      handleShutdown(signal).catch((err) => {
        logger.error('[SERVER] Unhandled error during shutdown', {
          message: err?.message || String(err)
        });
        process.exit(1);
      });
    });
  });
}

// Export for tests or external use
export default app;
export { server };
