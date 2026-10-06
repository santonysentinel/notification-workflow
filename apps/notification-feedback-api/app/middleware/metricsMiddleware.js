import { increment, timing, gauge } from '../services/metrics.js';

/**
 * Express middleware for HTTP request metrics collection
 * Tracks request count, response times, status codes, and error rates
 *
 * @param {Object} options - Middleware configuration options
 * @param {boolean} [options.includeUserAgent=false] - Include user agent in tags
 * @param {boolean} [options.includeIp=false] - Include client IP in tags
 * @param {Array<string>} [options.skipPaths=['/health', '/ping']] - Paths to skip metrics for
 * @param {Function} [options.pathNormalizer] - Function to normalize request paths
 * @returns {Function} Express middleware function
 */
export function metricsMiddleware(options = {}) {
  const {
    includeUserAgent = false,
    includeIp = false,
    skipPaths = ['/health', '/ping', '/favicon.ico'],
    pathNormalizer = defaultPathNormalizer
  } = options;

  return (req, res, next) => {
    const startTime = process.hrtime.bigint();
    const requestStarted = Date.now();

    // Skip metrics for certain paths
    if (skipPaths.includes(req.path)) {
      return next();
    }

    // Normalize the path for consistent metrics (replace IDs with placeholders)
    const normalizedPath = pathNormalizer(req.path, req.route?.path);

    // Base tags for all metrics
    const baseTags = {
      method: req.method,
      endpoint: normalizedPath,
      route: req.route?.path || 'unknown'
    };

    if (req.context?.traceId) {
      baseTags.trace_id = req.context.traceId;
    }

    if (req.context?.userId) {
      baseTags.user = req.context.userId;
    }

    // Optional tags
    if (includeUserAgent && req.get('User-Agent')) {
      baseTags.user_agent = parseUserAgent(req.get('User-Agent'));
    }

    if (includeIp && req.ip) {
      baseTags.client_ip = req.ip;
    }

    // Track request start
    increment('http.requests.total', 1, baseTags);
    increment('http.requests.in_flight', 1, baseTags);

    // Track concurrent requests
    gauge('http.requests.concurrent', getCurrentConcurrentRequests(), {
      method: req.method
    });

    // Override res.end to capture completion metrics
    const originalEnd = res.end;
    const originalJson = res.json;

    let ended = false;

    const recordMetrics = () => {
      if (ended) return; // Prevent double recording
      ended = true;

      // Calculate response time
      const endTime = process.hrtime.bigint();
      const durationMs = Number(endTime - startTime) / 1_000_000; // Convert to milliseconds

      const responseTags = {
        ...baseTags,
        status_code: res.statusCode.toString(),
        status_class: getStatusClass(res.statusCode)
      };

      // Record timing - both global and per-endpoint
      timing('http.request.duration', durationMs, responseTags);
      timing('http.request.duration.histogram', durationMs, responseTags);

      // Per-endpoint timing (clearer metric name)
      timing(
        `endpoint.${responseTags.method.toLowerCase()}.${normalizeMetricName(responseTags.endpoint)}.duration`,
        durationMs,
        {
          status_code: responseTags.status_code,
          status_class: responseTags.status_class
        }
      );

      // Record response
      increment('http.responses.total', 1, responseTags);

      // Decrement in-flight counter
      increment('http.requests.in_flight', -1, baseTags);

      // Track errors
      if (res.statusCode >= 400) {
        increment('http.requests.errors', 1, {
          ...responseTags,
          error_type: getErrorType(res.statusCode)
        });

        // Track specific error categories
        if (res.statusCode >= 500) {
          increment('http.requests.server_errors', 1, responseTags);
        } else if (res.statusCode >= 400) {
          increment('http.requests.client_errors', 1, responseTags);
        }
      }

      // Track success responses
      if (res.statusCode >= 200 && res.statusCode < 300) {
        increment('http.requests.success', 1, responseTags);

        // Per-endpoint success metric
        increment(
          `endpoint.${responseTags.method.toLowerCase()}.${normalizeMetricName(responseTags.endpoint)}.success`,
          1,
          {
            status_code: responseTags.status_code
          }
        );
      } else {
        // Per-endpoint failure metric
        increment(
          `endpoint.${responseTags.method.toLowerCase()}.${normalizeMetricName(responseTags.endpoint)}.failure`,
          1,
          {
            status_code: responseTags.status_code,
            error_type: getErrorType(res.statusCode)
          }
        );
      }

      // Track response size if available
      const contentLength = res.get('Content-Length');
      if (contentLength) {
        gauge('http.response.size.bytes', parseInt(contentLength), responseTags);
      }

      // Log slow requests (configurable threshold)
      const slowThreshold = process.env.SLOW_REQUEST_THRESHOLD_MS || 1000;
      if (durationMs > slowThreshold) {
        increment('http.requests.slow', 1, {
          ...responseTags,
          threshold: slowThreshold.toString()
        });
      }
    };

    // Override both end() and json() to catch all response completions
    res.end = function (...args) {
      recordMetrics();
      return originalEnd.apply(this, args);
    };

    res.json = function (...args) {
      recordMetrics();
      return originalJson.apply(this, args);
    };

    // Handle connection close/timeout
    req.on('close', () => {
      if (!ended) {
        increment('http.requests.aborted', 1, baseTags);
        recordMetrics();
      }
    });

    next();
  };
}

/**
 * Default path normalizer - replaces UUIDs and IDs with placeholders
 * @param {string} path - Original request path
 * @param {string} routePath - Express route path if available
 * @returns {string} Normalized path
 */
function defaultPathNormalizer(path, routePath) {
  if (routePath) {
    return routePath;
  }

  return (
    path
      // Replace UUIDs
      .replace(/\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '/:uuid')
      // Replace ULIDs
      .replace(/\/[0-9A-HJKMNP-TV-Z]{26}/g, '/:ulid')
      // Replace numeric IDs
      .replace(/\/\d+/g, '/:id')
      // Replace file extensions
      .replace(/\.[a-zA-Z0-9]+$/g, '.:ext')
  );
}

/**
 * Parse user agent into a simplified category
 * @param {string} userAgent - Full user agent string
 * @returns {string} Simplified user agent category
 */
function parseUserAgent(userAgent) {
  const ua = userAgent.toLowerCase();

  if (ua.includes('bot') || ua.includes('crawler') || ua.includes('spider')) {
    return 'bot';
  }
  if (ua.includes('postman')) return 'postman';
  if (ua.includes('curl')) return 'curl';
  if (ua.includes('wget')) return 'wget';
  if (ua.includes('chrome')) return 'chrome';
  if (ua.includes('firefox')) return 'firefox';
  if (ua.includes('safari')) return 'safari';
  if (ua.includes('edge')) return 'edge';

  return 'other';
}

/**
 * Get HTTP status class (1xx, 2xx, 3xx, 4xx, 5xx)
 * @param {number} statusCode - HTTP status code
 * @returns {string} Status class
 */
function getStatusClass(statusCode) {
  return `${Math.floor(statusCode / 100)}xx`;
}

/**
 * Normalize endpoint path for metric names (remove special characters)
 * @param {string} endpoint - Endpoint path
 * @returns {string} Normalized metric-safe name
 */
function normalizeMetricName(endpoint) {
  return (
    endpoint
      .replace(/^\//, '') // Remove leading slash
      .replace(/\//g, '_') // Replace slashes with underscores
      .replace(/:/g, '') // Remove colons from :id, :uuid
      .replace(/-/g, '_') // Replace hyphens with underscores
      .replace(/[^a-zA-Z0-9_]/g, '') // Remove any other special characters
      .toLowerCase() || 'root'
  );
}

/**
 * Get error type based on status code
 * @param {number} statusCode - HTTP status code
 * @returns {string} Error type
 */
function getErrorType(statusCode) {
  switch (statusCode) {
    case 400:
      return 'bad_request';
    case 401:
      return 'unauthorized';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    case 409:
      return 'conflict';
    case 422:
      return 'validation_error';
    case 429:
      return 'rate_limited';
    case 500:
      return 'internal_error';
    case 502:
      return 'bad_gateway';
    case 503:
      return 'service_unavailable';
    case 504:
      return 'timeout';
    default:
      return statusCode >= 500 ? 'server_error' : 'client_error';
  }
}

/**
 * Track concurrent requests (simplified implementation)
 * In production, you might want to use a more sophisticated approach
 * @returns {number} Current concurrent request count
 */
let concurrentRequestCounter = 0;
function getCurrentConcurrentRequests() {
  return concurrentRequestCounter;
}

// Helper middleware to track business-specific metrics
export function businessMetricsMiddleware(req, res, next) {
  const originalJson = res.json;

  res.json = function (data) {
    // Track API-specific metrics based on response data
    
    return originalJson.apply(this, arguments);
  };

  next();
}

export default metricsMiddleware;
