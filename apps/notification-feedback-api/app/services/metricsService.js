import nconf from 'nconf';
import { initializeFromConfig } from './metrics.js';

/**
 * Initialize StatsD metrics client for the file service API
 * Should be called once during application startup
 */
export function initializeMetrics() {
  try {
    // Initialize StatsD client from configuration
    const client = initializeFromConfig(nconf);

    console.log('[Metrics] StatsD client initialized for papp-coordinator-api');

    return client;
  } catch (error) {
    console.error('[Metrics] Failed to initialize StatsD client:', error);
    // Don't crash the app if metrics fail to initialize
    return null;
  }
}

/**
 * Add default StatsD configuration to nconf if not present
 * Call this before initializeMetrics() to ensure defaults are set
 */
export function setupDefaultMetricsConfig() {
  // Set default metrics configuration if not present
  let parsedGlobalTags = {};
  if (process.env.STATSD_GLOBAL_TAGS) {
    try {
      parsedGlobalTags = JSON.parse(process.env.STATSD_GLOBAL_TAGS);
    } catch (error) {
      console.warn('[Metrics] Failed to parse STATSD_GLOBAL_TAGS, using defaults', error);
    }
  }

  const defaults = {
    statsD: {
      host: process.env.STATSD_HOST || 'localhost',
      port: parseInt(process.env.STATSD_PORT) || 8125,
      protocol: process.env.STATSD_PROTOCOL || 'udp',
      prefix: process.env.STATSD_PREFIX || 'papp.coord.',
      mock: process.env.NODE_ENV === 'test',
      telegraf: process.env.STATSD_TELEGRAF === 'true',
      globalTags: {
        service: 'papp-coordinator-api',
        version: process.env.npm_package_version || '1.0.0',
        ...parsedGlobalTags
      },
      maxBufferSize: parseInt(process.env.STATSD_BUFFER_SIZE) || 1400,
      bufferFlushInterval: parseInt(process.env.STATSD_FLUSH_INTERVAL) || 1000
    }
  };

  // Add defaults to nconf
  nconf.defaults(defaults);

  console.log('[Metrics] Default StatsD configuration loaded');
}
