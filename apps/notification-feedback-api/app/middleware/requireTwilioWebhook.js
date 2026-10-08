import twilio from 'twilio';
import { sendError } from '../../configure/errors.js';

export default function requireTwilioWebhook({
  getConfig,
  urlKey,
  path,
  configProperty = 'twilioVoiceConfig',
  configurationError = 'Twilio voice webhook is not configured'
}) {
  return (req, res, next) => {
    const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
    if (!req.is('application/x-www-form-urlencoded')) {
      return sendError(res, 415, 'A form-urlencoded request is required', errorContext);
    }
    const config = getConfig();
    const configuredUrl = config[urlKey];
    let publicUrl;
    try {
      publicUrl = new URL(configuredUrl);
    } catch {
      return sendError(res, 503, configurationError, errorContext);
    }
    if (
      !/^AC[0-9a-fA-F]{32}$/.test(config.accountSid ?? '') ||
      typeof config.authToken !== 'string' ||
      !config.authToken.trim() ||
      publicUrl.protocol !== 'https:' ||
      publicUrl.search ||
      publicUrl.hash ||
      publicUrl.username ||
      publicUrl.password ||
      !publicUrl.pathname.endsWith(path) ||
      typeof config.database !== 'string' ||
      !config.database.trim()
    ) {
      return sendError(res, 503, configurationError, errorContext);
    }
    const signature = req.get('X-Twilio-Signature');
    const queryIndex = req.originalUrl.indexOf('?');
    const signedUrl = configuredUrl + (queryIndex < 0 ? '' : req.originalUrl.slice(queryIndex));
    const body = req.body;
    if (
      !signature ||
      !body ||
      Object.values(body).some((value) => typeof value !== 'string') ||
      !twilio.validateRequest(config.authToken, signature, signedUrl, body) ||
      body.AccountSid !== config.accountSid
    ) {
      return sendError(res, 403, 'Invalid Twilio callback', errorContext);
    }
    req[configProperty] = config;
    return next();
  };
}
