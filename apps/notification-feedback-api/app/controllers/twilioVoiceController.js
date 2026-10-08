import * as repository from '../../../../pkg/repository/automatedCallRepository.js';
import { sendError } from '../../configure/errors.js';
import { isVoiceResponse, prepareOpeningStep } from '../utils/twilioTwiML.js';
import { prepareNextStep } from '../utils/twilioTransitions.js';

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
      const options = { platform: config.database, timeoutMs: 5000 };
      const twiML = await repo.getAutomatedCallTwiML({ callId }, options);
      const openingStep = prepareOpeningStep(twiML);
      const result = await repo.startAutomatedCall(
        {
          callId,
          providerCallId: CallSid,
          phoneE164: To,
          startedCallStatus: config.startedCallStatus,
          pendingCallStatus: config.pendingCallStatus,
          openingStep
        },
        options
      );
      if (result.Outcome === 'not-found') {
        return sendError(res, 404, 'Automated call not found', errorContext);
      }
      if (
        [
          'provider-mismatch',
          'destination-mismatch',
          'call-sid-mismatch',
          'bundle-changed'
        ].includes(result.Outcome)
      ) {
        return sendError(res, 409, 'Callback does not match the automated call', errorContext);
      }
      if (result.Outcome !== 'started' || !isVoiceResponse(result.ResponseTwiML)) {
        return sendError(res, 500, 'Automated call instructions are unavailable', errorContext);
      }
      return res
        .status(200)
        .set('Cache-Control', 'no-store')
        .type('application/xml')
        .send(result.ResponseTwiML);
    } catch (err) {
      req.log?.error('twilio.voice.start.failed', { message: err?.message, callId });
      return sendError(res, 500, 'Failed to start automated call', errorContext);
    }
  };
}

export function createVoiceNextController(repo = repository) {
  return async (req, res) => {
    const errorContext = { traceId: req.context?.traceId, path: req.originalUrl };
    const rawCallId = req.query.callId;
    const callId =
      typeof rawCallId === 'string' && /^[1-9]\d*$/.test(rawCallId) ? Number(rawCallId) : NaN;
    const executionId = req.query.executionId;
    const { CallSid, To, Digits = '', SpeechResult = '', Confidence } = req.body;
    if (!Number.isInteger(callId) || callId > 2147483647) {
      return sendError(res, 400, 'callId must be a positive SQL integer', errorContext);
    }
    if (
      typeof executionId !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(executionId)
    ) {
      return sendError(res, 400, 'A valid executionId is required', errorContext);
    }
    if (typeof CallSid !== 'string' || !/^CA[0-9a-fA-F]{32}$/.test(CallSid)) {
      return sendError(res, 400, 'A valid CallSid is required', errorContext);
    }
    if (typeof To !== 'string' || !/^\+[1-9]\d{1,14}$/.test(To)) {
      return sendError(res, 400, 'A valid E.164 To number is required', errorContext);
    }
    if (
      !/^[0-9*#]*$/.test(Digits) ||
      (Confidence !== undefined &&
        (!Confidence.trim() ||
          !Number.isFinite(Number(Confidence)) ||
          Number(Confidence) < 0 ||
          Number(Confidence) > 1))
    ) {
      return sendError(res, 400, 'Invalid gather input', errorContext);
    }
    const input = {
      digits: Digits,
      speechResult: SpeechResult,
      confidence: Confidence === undefined ? null : Number(Confidence)
    };
    try {
      const options = { platform: req.twilioVoiceConfig.database, timeoutMs: 5000 };
      const context = await repo.getAutomatedCallNextContext({ callId, executionId }, options);
      let sourceStepId;
      try {
        sourceStepId = JSON.parse(context?.SourceEventJSON)?.stepId;
      } catch {
        sourceStepId = undefined;
      }
      const nextStep = prepareNextStep(context?.TwiML, context?.TemplateJSON, sourceStepId, input);
      const result = await repo.advanceAutomatedCall(
        {
          callId,
          executionId,
          providerCallId: CallSid,
          phoneE164: To,
          input,
          nextStep
        },
        options
      );
      if (['not-found', 'execution-not-found'].includes(result.Outcome)) {
        return sendError(res, 404, 'Automated call execution not found', errorContext);
      }
      if (
        [
          'provider-mismatch',
          'destination-mismatch',
          'call-sid-mismatch',
          'stale-execution',
          'context-changed'
        ].includes(result.Outcome)
      ) {
        return sendError(
          res,
          409,
          'Callback does not match the current automated call execution',
          errorContext
        );
      }
      if (result.Outcome !== 'advanced' || !isVoiceResponse(result.ResponseTwiML)) {
        return sendError(res, 500, 'Automated call instructions are unavailable', errorContext);
      }
      return res
        .status(200)
        .set('Cache-Control', 'no-store')
        .type('application/xml')
        .send(result.ResponseTwiML);
    } catch (err) {
      req.log?.error('twilio.voice.next.failed', { message: err?.message, callId, executionId });
      return sendError(res, 500, 'Failed to advance automated call', errorContext);
    }
  };
}
