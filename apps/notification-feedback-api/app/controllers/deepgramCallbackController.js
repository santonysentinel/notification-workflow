import { timingSafeEqual } from 'node:crypto';
import * as repository from '../../../../pkg/repository/automatedCallRepository.js';
import { sendError } from '../../configure/errors.js';
import { normalizeDeepgramCallback } from '../utils/deepgramTranscription.js';

export function createDeepgramCallbackController(repo = repository) {
  return async (req, res) => {
    const context = { traceId: req.context?.traceId, path: req.path };
    const auth = req.deepgramCallback;
    const options = { platform: auth.database, timeoutMs: 5000 };
    try {
      const submission = await repo.getDeepgramCallbackContext(
        { correlationId: auth.correlationId },
        options
      );
      if (
        !submission ||
        submission.RecordingId !== auth.recordingId ||
        !Buffer.isBuffer(submission.CallbackTokenHash) ||
        submission.CallbackTokenHash.length !== 32 ||
        !timingSafeEqual(submission.CallbackTokenHash, auth.tokenHash)
      ) {
        return sendError(res, 401, 'Invalid callback authentication', context);
      }
      const normalized = normalizeDeepgramCallback(req.body, submission);
      if (!normalized)
        return sendError(res, 400, 'Invalid transcription callback payload', context);
      const result = await repo.completeDeepgramCallback(
        {
          ...normalized,
          correlationId: auth.correlationId,
          recordingId: auth.recordingId,
          tokenHash: auth.tokenHash
        },
        options
      );
      if (result.Outcome === 'unauthorized')
        return sendError(res, 401, 'Invalid callback authentication', context);
      if (['request-conflict', 'payload-conflict'].includes(result.Outcome)) {
        return sendError(res, 409, 'Callback conflicts with a saved result', context);
      }
      if (!['recorded', 'duplicate'].includes(result.Outcome)) throw new Error('Unexpected result');
      return res.status(204).end();
    } catch {
      req.log?.error('deepgram.callback.persistence.failed', { correlationId: auth.correlationId });
      return sendError(res, 500, 'Transcription callback could not be saved', context);
    }
  };
}
