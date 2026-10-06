import StatsD from 'hot-shots';
import os from 'node:os';

/**
 * Shared StatsD client instance
 * @type {StatsD|null}
 */
let statsClient = null;

/**
 * Default StatsD configuration
 */
const defaultConfig = {
  host: 'localhost',
  port: 8125,
  protocol: 'udp',
  globalTags: {},
  prefix: 'sdms.',
  suffix: '',
  telegraf: false,
  mock: false,
  maxBufferSize: 1400,
  bufferFlushInterval: 1000,
  errorHandler: (error) => {
    console.error('[StatsD] Error:', error);
  }
};

/**
 * Initialize the StatsD client with configuration
 * @param {Object} config - StatsD configuration options
 * @param {string} [config.host='localhost'] - StatsD server host
 * @param {number} [config.port=8125] - StatsD server port
 * @param {string} [config.protocol='udp'] - Protocol (udp/tcp)
 * @param {Object} [config.globalTags={}] - Tags applied to all metrics
 * @param {string} [config.prefix='sdms.'] - Metric name prefix
 * @param {string} [config.suffix=''] - Metric name suffix
 * @param {boolean} [config.telegraf=false] - Telegraf compatibility mode
 * @param {boolean} [config.mock=false] - Mock mode for testing
 * @param {number} [config.maxBufferSize=1400] - UDP buffer size
 * @param {number} [config.bufferFlushInterval=1000] - Buffer flush interval (ms)
 * @param {Function} [config.errorHandler] - Error handling callback
 * @returns {StatsD} Initialized StatsD client
 */
export function initializeStatsD(config = {}) {
  const mergedConfig = {
    ...defaultConfig,
    ...config,
    globalTags: {
      ...defaultConfig.globalTags,
      ...config.globalTags
    }
  };

  // Add service name to global tags if not present
  if (!mergedConfig.globalTags.service) {
    mergedConfig.globalTags.service = process.env.SERVICE_NAME || 'unknown';
  }

  // Add environment to global tags if not present
  if (!mergedConfig.globalTags.env) {
    mergedConfig.globalTags.env = process.env.NODE_ENV || 'development';
  }

  // Add instance/hostname to global tags
  if (!mergedConfig.globalTags.instance) {
    mergedConfig.globalTags.instance = process.env.HOSTNAME || os.hostname();
  }

  statsClient = new StatsD(mergedConfig);

  return statsClient;
}

/**
 * Initialize StatsD from nconf configuration
 * @param {Object} nconf - nconf configuration object
 * @returns {StatsD} Initialized StatsD client
 */
export function initializeFromConfig(nconf) {
  const config = {
    host: nconf.get('statsD:host'),
    port: nconf.get('statsD:port'),
    protocol: nconf.get('statsD:protocol'),
    prefix: nconf.get('statsD:prefix'),
    suffix: nconf.get('statsD:suffix'),
    telegraf: nconf.get('statsD:telegraf'),
    mock: nconf.get('statsD:mock'),
    maxBufferSize: nconf.get('statsD:maxBufferSize'),
    bufferFlushInterval: nconf.get('statsD:bufferFlushInterval'),
    globalTags: nconf.get('statsD:globalTags') || {}
  };

  // Filter out undefined values
  const cleanConfig = Object.fromEntries(
    Object.entries(config).filter(([_, value]) => value !== undefined)
  );

  return initializeStatsD(cleanConfig);
}

/**
 * Get the current StatsD client instance
 * @returns {StatsD|null} StatsD client or null if not initialized
 */
export function getStatsClient() {
  return statsClient;
}

/**
 * Ensure StatsD client is initialized, throw if not
 * @returns {StatsD} StatsD client
 * @throws {Error} If client is not initialized
 */
export function requireStatsClient() {
  if (!statsClient) {
    throw new Error('StatsD client not initialized. Call initializeStatsD() first.');
  }
  return statsClient;
}

/**
 * Helper function to increment a counter
 * @param {string} metric - Metric name
 * @param {number} [value=1] - Increment value
 * @param {Object} [tags={}] - Additional tags
 */
export function increment(metric, value = 1, tags = {}) {
  const client = getStatsClient();
  if (client) {
    client.increment(metric, value, tags);
  }
}

/**
 * Helper function to set a gauge value
 * @param {string} metric - Metric name
 * @param {number} value - Gauge value
 * @param {Object} [tags={}] - Additional tags
 */
export function gauge(metric, value, tags = {}) {
  const client = getStatsClient();
  if (client) {
    client.gauge(metric, value, tags);
  }
}

/**
 * Helper function to record a histogram/timing value
 * @param {string} metric - Metric name
 * @param {number} value - Timing value in milliseconds
 * @param {Object} [tags={}] - Additional tags
 */
export function histogram(metric, value, tags = {}) {
  const client = getStatsClient();
  if (client) {
    client.histogram(metric, value, tags);
  }
}

/**
 * Helper function to record timing
 * @param {string} metric - Metric name
 * @param {number} value - Timing value in milliseconds
 * @param {Object} [tags={}] - Additional tags
 */
export function timing(metric, value, tags = {}) {
  const client = getStatsClient();
  if (client) {
    client.timing(metric, value, tags);
  }
}

/**
 * Helper function to create a timer that automatically records elapsed time
 * @param {string} metric - Metric name
 * @param {Object} [tags={}] - Additional tags
 * @returns {Function} End function that records the elapsed time
 */
export function timer(metric, tags = {}) {
  const start = Date.now();
  return () => {
    const elapsed = Date.now() - start;
    timing(metric, elapsed, tags);
    return elapsed;
  };
}

/**
 * Close the StatsD client connection
 */
export function close() {
  if (statsClient) {
    statsClient.close();
    statsClient = null;
    console.log('[StatsD] Client closed');
  }
}

// Default export for convenience
export default {
  initializeStatsD,
  initializeFromConfig,
  getStatsClient,
  requireStatsClient,
  increment,
  gauge,
  histogram,
  timing,
  timer,
  close
};
