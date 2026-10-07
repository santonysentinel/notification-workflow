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

  const { activeAlarmId, flowId, callStatus, priority, availableAt, maxAttempts } = body;
  const oid = req.oid ?? body.oid;

  if (!Number.isInteger(activeAlarmId) || !Number.isInteger(flowId)) {
    return sendError(res, 400, 'activeAlarmId and flowId must be integers', { traceId, path });
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
      { activeAlarmId, oid, flowId, callStatus, priority, availableAt: scheduledAt, maxAttempts },
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
      err?.httpStatus ?? 500,
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
