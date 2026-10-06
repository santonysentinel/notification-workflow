import winston from 'winston';
import nconf from 'nconf';
import envConfig from './configuration.js';

// Load config
nconf
  .argv()
  .env()
  .file({ file: `./${envConfig.configFile}` });

const serviceName = nconf.get('service:name') || 'papp-coordinatior-api';
const environment = nconf.get('NODE_ENV') || process.env.NODE_ENV || 'development';

const logger = winston.createLogger({
  level: nconf.get('logging:level') || process.env.LOG_LEVEL || 'info',
  defaultMeta: {
    service: serviceName,
    environment
  },
  format: winston.format.combine(winston.format.timestamp(), winston.format.json()),
  transports: [
    new winston.transports.Console({
      handleExceptions: true
    })
  ],
  exitOnError: false
});

export const createLogger = (meta = {}) => logger.child(meta);

const accessLogStream = {
  write: (message) => {
    const line = message.trim();
    if (line) {
      logger.info(line, { channel: 'access' });
    }
  }
};

export { logger as Logger, accessLogStream as MorganAccessStream };
