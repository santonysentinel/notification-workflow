import express from 'express';
import nconf from 'nconf';
import twilio from 'twilio';
import { createVoiceStartController } from '../controllers/twilioVoiceController.js';
import { sendError } from '../../configure/errors.js';

function readConfig() {
  const pending = process.env.TWILIO_PENDING_CALL_STATUS ?? nconf.get('twilio:pendingCallStatus');
  return {
    accountSid: process.env.TWILIO_ACCOUNT_SID ?? nconf.get('twilio:accountSid'),
    authToken: process.env.TWILIO_AUTH_TOKEN ?? nconf.get('twilio:authToken'),
    startUrl: process.env.TWILIO_VOICE_START_URL ?? nconf.get('twilio:voiceStartUrl'),
    database: process.env.AUTO_CALL_DATABASE ?? nconf.get('twilio:database') ?? 'AutoCallDB',
    startedCallStatus: Number(
      process.env.TWILIO_STARTED_CALL_STATUS ?? nconf.get('twilio:startedCallStatus')
    ),
    pendingCallStatus: pending == null ? null : Number(pending)
  };
}

export function createTwilioVoiceRouter({ repository, getConfig = readConfig } = {}) {
  const router = express.Router();
  router.post(
    '/start',
    express.urlencoded({ extended: false, limit: '32kb' }),
    (req, res, next) => {
      const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
      if (!req.is('application/x-www-form-urlencoded')) {
        return sendError(res, 415, 'A form-urlencoded request is required', errorContext);
      }
      const config = getConfig();
      let publicUrl;
      try {
        publicUrl = new URL(config.startUrl);
      } catch {
        return sendError(res, 503, 'Twilio voice webhook is not configured', errorContext);
      }
      const validStatus = (status) =>
        Number.isInteger(status) && status > 0 && status <= 2147483647;
      if (
        !/^AC[0-9a-fA-F]{32}$/.test(config.accountSid ?? '') ||
        typeof config.authToken !== 'string' ||
        !config.authToken.trim() ||
        publicUrl.protocol !== 'https:' ||
        publicUrl.search ||
        publicUrl.hash ||
        publicUrl.username ||
        publicUrl.password ||
        !publicUrl.pathname.endsWith('/webhooks/twilio/voice/start') ||
        typeof config.database !== 'string' ||
        !config.database.trim() ||
        !validStatus(config.startedCallStatus) ||
        (config.pendingCallStatus !== null && !validStatus(config.pendingCallStatus))
      ) {
        return sendError(res, 503, 'Twilio voice webhook is not configured', errorContext);
      }
      const signature = req.get('X-Twilio-Signature');
      const queryIndex = req.originalUrl.indexOf('?');
      const signedUrl = config.startUrl + (queryIndex < 0 ? '' : req.originalUrl.slice(queryIndex));
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
      req.twilioVoiceConfig = config;
      return next();
    },
    createVoiceStartController(repository)
  );
  return router;
}

export default createTwilioVoiceRouter();
