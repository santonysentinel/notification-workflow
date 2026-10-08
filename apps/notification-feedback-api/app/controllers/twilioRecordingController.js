import * as repository from '../../../../pkg/repository/automatedCallRepository.js';
import { sendError } from '../../configure/errors.js';
import { parseRecordingCallback } from '../utils/twilioRecordings.js';

export function createRecordingStatusController(repo = repository) {
  return async (req, res) => {
    const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
    const rawCallId = req.query.callId;
    const callId =
      typeof rawCallId === 'string' && /^[1-9]\d*$/.test(rawCallId) ? Number(rawCallId) : NaN;
    if (!Number.isInteger(callId) || callId > 2147483647) {
      return sendError(res, 400, 'callId must be a positive SQL integer', errorContext);
    }
    const recording = parseRecordingCallback(req.body);
    if (!recording) return sendError(res, 400, 'Invalid recording status callback', errorContext);
    try {
      const result = await repo.recordAutomatedCallRecordingStatus(
        {
          callId,
          ...recording,
          parameters: req.body
        },
        { platform: req.twilioRecordingConfig.database, timeoutMs: 5000 }
      );
      if (result.Outcome === 'not-found')
        return sendError(res, 404, 'Automated call not found', errorContext);
      if (
        ['provider-mismatch', 'call-sid-mismatch', 'recording-conflict'].includes(result.Outcome)
      ) {
        return sendError(
          res,
          409,
          'Recording callback does not match the automated call',
          errorContext
        );
      }
      if (!['recorded', 'duplicate'].includes(result.Outcome)) {
        return sendError(res, 500, 'Recording status could not be recorded', errorContext);
      }
      return res.status(204).set('Cache-Control', 'no-store').end();
    } catch (err) {
      req.log?.error('twilio.recordings.status.failed', {
        message: err?.message,
        callId,
        recordingSid: recording.recordingSid
      });
      return sendError(res, 500, 'Failed to record recording status', errorContext);
    }
  };
}
