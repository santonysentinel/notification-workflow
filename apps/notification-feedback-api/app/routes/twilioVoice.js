import express from 'express';
import nconf from 'nconf';
import requireTwilioWebhook from '../middleware/requireTwilioWebhook.js';
import {
  createVoiceStartController,
  createVoiceNextController,
  createVoiceStatusController
} from '../controllers/twilioVoiceController.js';

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
      requireTwilioWebhook({
        getConfig,
        urlKey: `${endpoint}Url`,
        path: `/webhooks/twilio/voice/${endpoint}`
      }),
      createController(repository)
    );
  }
  return router;
}

export default createTwilioVoiceRouter();
