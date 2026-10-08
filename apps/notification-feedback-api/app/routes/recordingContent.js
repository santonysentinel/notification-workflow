import express from 'express';
import { createRecordingContentController } from '../controllers/recordingContentController.js';

export function mountRecordingContentRoute(app, options) {
  const router = express.Router();
  router.get('/:recordingId/content', createRecordingContentController(options));
  app.use('/api/v1/auto-call/recordings', router);
}
