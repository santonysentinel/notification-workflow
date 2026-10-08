import express from 'express';
import nconf from 'nconf';
import requireTwilioWebhook from '../middleware/requireTwilioWebhook.js';
import { createRecordingStatusController } from '../controllers/twilioRecordingController.js';

function readConfig() {
  return {
    accountSid: process.env.TWILIO_ACCOUNT_SID ?? nconf.get('twilio:accountSid'),
    authToken: process.env.TWILIO_AUTH_TOKEN ?? nconf.get('twilio:authToken'),
    recordingStatusUrl:
      process.env.TWILIO_RECORDING_STATUS_URL ?? nconf.get('twilio:recordingStatusUrl'),
    database: process.env.AUTO_CALL_DATABASE ?? nconf.get('twilio:database') ?? 'AutoCallDB'
  };
}

export function createTwilioRecordingsRouter({ repository, getConfig = readConfig } = {}) {
  const router = express.Router();
  router.post(
    '/status',
    express.urlencoded({ extended: false, limit: '32kb' }),
    requireTwilioWebhook({
      getConfig,
      urlKey: 'recordingStatusUrl',
      path: '/webhooks/twilio/recordings/status',
      configProperty: 'twilioRecordingConfig',
      configurationError: 'Twilio recording webhook is not configured'
    }),
    createRecordingStatusController(repository)
  );
  return router;
}

export default createTwilioRecordingsRouter();
