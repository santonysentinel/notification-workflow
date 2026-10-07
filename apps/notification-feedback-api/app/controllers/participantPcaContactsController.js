import * as repository from '@pappservice/repository';
import { sendError } from '../../configure/errors.js';

let repo = repository;

export const __setRepositoryForTests = (mockRepo) => {
  repo = mockRepo;
};

export const __resetRepositoryForTests = () => {
  repo = repository;
};

function normalizeParticipantId(participantId) {
  if (typeof participantId !== 'string') {
    return null;
  }

  const trimmed = participantId.trim();
  return trimmed ? trimmed : null;
}

export async function getParticipantPcaContacts(req, res) {
  const traceId = req.context?.traceId;
  const path = req.originalUrl;
  const log = req.log || console;

  const participantId = normalizeParticipantId(req.params?.participantId);
  if (!participantId) {
    return sendError(res, 400, 'participantId is required', {
      traceId,
      path,
      details: 'Path parameter participantId is missing or invalid.'
    });
  }

  try {
    const platform = req.routing?.Platform ?? req.routing?.platform;
    const contacts = await repo.getPCAContacts(
      {
        OID: req.oid
      },
      { platform }
    );

    if (typeof req.setContext === 'function') {
      req.setContext({
        participantId
      });
    }

    return res.status(200).json(contacts);
  } catch (err) {
    log.error?.('participantPcaContacts.fetch.failed', {
      message: err?.message,
      stack: err?.stack,
      traceId,
      participantId
    });

    const status = err?.httpStatus ?? 500;
    const message = err?.message || 'Failed to fetch participant companion app contacts';

    const details = err?.sqlState ? { sqlState: err.sqlState } : undefined;

    return sendError(res, status, message, {
      traceId,
      path,
      details
    });
  }
}

export default {
  getParticipantPcaContacts
};
