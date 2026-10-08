import express from 'express';
import nconf from 'nconf';
import twilio from 'twilio';
import {
  createVoiceStartController,
  createVoiceNextController,
  createVoiceStatusController
} from '../controllers/twilioVoiceController.js';
import { sendError } from '../../configure/errors.js';

function readConfig() {
  return {
    accountSid: process.env.TWILIO_ACCOUNT_SID ?? nconf.get('twilio:accountSid'),
    authToken: process.env.TWILIO_AUTH_TOKEN ?? nconf.get('twilio:authToken'),
    startUrl: process.env.TWILIO_VOICE_START_URL ?? nconf.get('twilio:voiceStartUrl'),
    nextUrl: process.env.TWILIO_VOICE_NEXT_URL ?? nconf.get('twilio:voiceNextUrl'),
    statusUrl: process.env.TWILIO_VOICE_STATUS_URL ?? nconf.get('twilio:voiceStatusUrl'),
    database: process.env.AUTO_CALL_DATABASE ?? nconf.get('twilio:database') ?? 'AutoCallDB'
  };
}

export function createTwilioVoiceRouter({ repository, getConfig = readConfig } = {}) {
  const router = express.Router();
  for (const [endpoint, createController] of [
    ['start', createVoiceStartController],
    ['next', createVoiceNextController],
    ['status', createVoiceStatusController]
  ]) {
    router.post(
      `/${endpoint}`,
      express.urlencoded({ extended: false, limit: '32kb' }),
      (req, res, next) => {
        const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
        if (!req.is('application/x-www-form-urlencoded')) {
          return sendError(res, 415, 'A form-urlencoded request is required', errorContext);
        }
        const config = getConfig();
        const configuredUrl = config[`${endpoint}Url`];
        let publicUrl;
        try {
          publicUrl = new URL(configuredUrl);
        } catch {
          return sendError(res, 503, 'Twilio voice webhook is not configured', errorContext);
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
          !publicUrl.pathname.endsWith(`/webhooks/twilio/voice/${endpoint}`) ||
          typeof config.database !== 'string' ||
          !config.database.trim()
        ) {
          return sendError(res, 503, 'Twilio voice webhook is not configured', errorContext);
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
        req.twilioVoiceConfig = config;
        return next();
      },
      createController(repository)
    );
  }
  return router;
}

export default createTwilioVoiceRouter();
