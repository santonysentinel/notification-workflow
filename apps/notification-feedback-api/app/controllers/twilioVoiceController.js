import * as repository from '../../../../pkg/repository/automatedCallRepository.js';
import { sendError } from '../../configure/errors.js';

export function createVoiceStartController(repo = repository) {
  return async (req, res) => {
    const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
    const rawCallId = req.query.callId;
    const callId =
      typeof rawCallId === 'string' && /^[1-9]\d*$/.test(rawCallId) ? Number(rawCallId) : NaN;
    const { CallSid, To } = req.body;
    if (!Number.isInteger(callId) || callId > 2147483647) {
      return sendError(res, 400, 'callId must be a positive SQL integer', errorContext);
    }
    if (typeof CallSid !== 'string' || !/^CA[0-9a-fA-F]{32}$/.test(CallSid)) {
      return sendError(res, 400, 'A valid CallSid is required', errorContext);
    }
    if (typeof To !== 'string' || !/^\+[1-9]\d{1,14}$/.test(To)) {
      return sendError(res, 400, 'A valid E.164 To number is required', errorContext);
    }

    try {
      const config = req.twilioVoiceConfig;
      const result = await repo.startAutomatedCall(
        {
          callId,
          providerCallId: CallSid,
          phoneE164: To,
          startedCallStatus: config.startedCallStatus,
          pendingCallStatus: config.pendingCallStatus
        },
        { platform: config.database, timeoutMs: 5000 }
      );
      if (result.Outcome === 'not-found') {
        return sendError(res, 404, 'Automated call not found', errorContext);
      }
      if (
        ['provider-mismatch', 'destination-mismatch', 'call-sid-mismatch'].includes(result.Outcome)
      ) {
        return sendError(res, 409, 'Callback does not match the automated call', errorContext);
      }
      if (
        result.Outcome !== 'started' ||
        typeof result.TwiML !== 'string' ||
        !result.TwiML.trim()
      ) {
        return sendError(res, 500, 'Automated call instructions are unavailable', errorContext);
      }
      return res
        .status(200)
        .set('Cache-Control', 'no-store')
        .type('application/xml')
        .send(result.TwiML);
    } catch (err) {
      req.log?.error('twilio.voice.start.failed', { message: err?.message, callId });
      return sendError(res, 500, 'Failed to start automated call', errorContext);
    }
  };
}
