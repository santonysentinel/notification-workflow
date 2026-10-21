import * as repository from '../../../../pkg/repository/activeAlarmParserRepository.js';
import { sendError } from '../../configure/errors.js';

let repo = repository;

export const __setRepositoryForTests = (mockRepo) => {
  repo = mockRepo;
};

export const __resetRepositoryForTests = () => {
  repo = repository;
};

export async function createAutoCall(req, res) {
  const traceId = req.context?.traceId;
  const path = req.originalUrl;
  const log = req.log || console;
  const body = req.body;

  if (!body || typeof body !== 'object' || Array.isArray(body)) {
    return sendError(res, 400, 'A JSON request body is required', { traceId, path });
  }

  const {
    activeAlarmId,
    flowId,
    variables,
    enqueueIdempotencyKey,
    callStatus,
    priority,
    availableAt,
    maxAttempts,
    options
  } = body;
  const oid = req.oid ?? body.oid;

  if (
    options !== undefined &&
    (!options ||
      typeof options !== 'object' ||
      Array.isArray(options) ||
      Object.keys(options).length !== 2 ||
      !Number.isInteger(options.historyId) ||
      options.historyId <= 0 ||
      options.historyId > 2147483647 ||
      typeof options.postRecordingTranscript !== 'boolean')
  ) {
    return sendError(
      res,
      400,
      'options requires a positive historyId and boolean postRecordingTranscript',
      { traceId, path }
    );
  }

  if (
    !Number.isInteger(activeAlarmId) ||
    activeAlarmId <= 0 ||
    !Number.isInteger(flowId) ||
    flowId <= 0
  ) {
    return sendError(res, 400, 'activeAlarmId and flowId must be positive integers', {
      traceId,
      path
    });
  }

  if (!variables || typeof variables !== 'object' || Array.isArray(variables)) {
    return sendError(res, 400, 'variables must be a JSON object', { traceId, path });
  }

  if (
    typeof enqueueIdempotencyKey !== 'string' ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      enqueueIdempotencyKey
    ) ||
    enqueueIdempotencyKey === '00000000-0000-0000-0000-000000000000'
  ) {
    return sendError(res, 400, 'enqueueIdempotencyKey must be a nonzero UUID', { traceId, path });
  }

  if (typeof oid !== 'string' || !oid.trim() || oid.length > 20) {
    return sendError(res, 400, 'oid must be a non-empty string of at most 20 characters', {
      traceId,
      path
    });
  }

  for (const [name, value] of Object.entries({ callStatus, priority, maxAttempts })) {
    if (value !== undefined && !(name === 'callStatus' && value === null)) {
      if (!Number.isInteger(value) || (name === 'maxAttempts' && value <= 0)) {
        return sendError(
          res,
          400,
          `${name} must be ${name === 'maxAttempts' ? 'a positive' : 'an'} integer`,
          {
            traceId,
            path
          }
        );
      }
    }
  }

  let scheduledAt = availableAt;
  if (availableAt != null) {
    if (typeof availableAt !== 'string' || Number.isNaN(Date.parse(availableAt))) {
      return sendError(res, 400, 'availableAt must be a valid date-time string', {
        traceId,
        path
      });
    }
    scheduledAt = new Date(availableAt);
  }

  try {
    const platform = req.routing?.Platform ?? req.routing?.platform;
    const systemId = await repo.addToAutomatedCallQueue(
      {
        activeAlarmId,
        oid,
        flowId,
        variables,
        enqueueIdempotencyKey,
        callStatus,
        priority,
        availableAt: scheduledAt,
        maxAttempts,
        options
      },
      { platform }
    );

    return res.status(201).json({ systemId });
  } catch (err) {
    log.error?.('autoCall.create.failed', {
      message: err?.message,
      stack: err?.stack,
      traceId,
      activeAlarmId,
      flowId
    });

    return sendError(
      res,
      err?.sqlState?.number === 51001 ? 409 : (err?.httpStatus ?? 500),
      err?.message || 'Failed to create automated call',
      {
        traceId,
        path,
        details: err?.sqlState ? { sqlState: err.sqlState } : undefined
      }
    );
  }
}

export default {
  createAutoCall
};
