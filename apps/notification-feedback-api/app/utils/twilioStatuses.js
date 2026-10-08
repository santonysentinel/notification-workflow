export const callStatuses = new Set([
  'queued',
  'initiated',
  'ringing',
  'in-progress',
  'completed',
  'busy',
  'failed',
  'no-answer',
  'canceled'
]);

function nonnegativeInteger(value) {
  if (typeof value !== 'string' || !/^(0|[1-9]\d*)$/.test(value)) return null;
  const number = Number(value);
  return Number.isInteger(number) && number <= 2147483647 ? number : null;
}

export function parseStatusCallback(body) {
  if (!callStatuses.has(body.CallStatus)) return null;
  const sequenceNumber = nonnegativeInteger(body.SequenceNumber);
  const timestampText = typeof body.Timestamp === 'string' ? body.Timestamp : '';
  const timestamp =
    /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun), \d{2} (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d{4} \d{2}:\d{2}:\d{2} (\+0000|GMT|UTC)$/.test(
      timestampText
    )
      ? new Date(timestampText)
      : null;
  if (
    sequenceNumber === null ||
    !timestamp ||
    !Number.isFinite(timestamp.getTime()) ||
    timestamp.toUTCString() !== timestampText.replace(/(\+0000|UTC)$/, 'GMT')
  )
    return null;
  const callDurationSeconds =
    body.CallDuration === undefined ? null : nonnegativeInteger(body.CallDuration);
  const sipResponseCode =
    body.SipResponseCode === undefined ? null : nonnegativeInteger(body.SipResponseCode);
  if (
    (body.CallDuration !== undefined && callDurationSeconds === null) ||
    (body.SipResponseCode !== undefined &&
      (sipResponseCode === null || sipResponseCode < 100 || sipResponseCode > 699))
  )
    return null;
  return {
    callStatus: body.CallStatus,
    sequenceNumber,
    timestamp,
    callDurationSeconds,
    sipResponseCode
  };
}
