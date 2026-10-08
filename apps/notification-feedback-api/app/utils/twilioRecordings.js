const statuses = new Set(['in-progress', 'completed', 'absent', 'failed']);

function optionalInteger(value) {
  if (value === undefined || value === '') return null;
  if (typeof value !== 'string' || !/^(0|[1-9]\d*)$/.test(value)) return NaN;
  const number = Number(value);
  return Number.isInteger(number) && number <= 2147483647 ? number : NaN;
}

function parseStartTime(value) {
  if (value === undefined || value === '') return null;
  if (typeof value !== 'string') return false;
  const timestamp = new Date(value);
  if (!Number.isFinite(timestamp.getTime()) || timestamp.getUTCFullYear() < 1) return false;
  if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/.test(value)) {
    return timestamp.toISOString() === value.replace(/(?<!\.\d{3})Z$/, '.000Z') ? timestamp : false;
  }
  return timestamp.toUTCString() === value.replace(/(\+0000|UTC)$/, 'GMT') ? timestamp : false;
}

export function parseRecordingCallback(body) {
  if (
    !/^AC[0-9a-fA-F]{32}$/.test(body.AccountSid ?? '') ||
    !/^CA[0-9a-fA-F]{32}$/.test(body.CallSid ?? '') ||
    !/^RE[0-9a-fA-F]{32}$/.test(body.RecordingSid ?? '') ||
    !statuses.has(body.RecordingStatus)
  )
    return null;
  const durationSeconds = optionalInteger(body.RecordingDuration);
  const channels = optionalInteger(body.RecordingChannels);
  const recordingStartTime = parseStartTime(body.RecordingStartTime);
  if (
    Number.isNaN(durationSeconds) ||
    Number.isNaN(channels) ||
    (channels !== null && channels !== 1 && channels !== 2) ||
    recordingStartTime === false
  )
    return null;
  const recordingSource = body.RecordingSource || null;
  const recordingTrack = body.RecordingTrack || null;
  if (
    (recordingSource !== null &&
      (typeof recordingSource !== 'string' || recordingSource.length > 100)) ||
    (recordingTrack !== null && !['inbound', 'outbound', 'both'].includes(recordingTrack))
  )
    return null;
  const recordingUrl = body.RecordingUrl || null;
  if (recordingUrl !== null) {
    if (typeof recordingUrl !== 'string' || recordingUrl.length > 2048) return null;
    let url;
    try {
      url = new URL(recordingUrl);
    } catch {
      return null;
    }
    const path = `/2010-04-01/Accounts/${body.AccountSid}/`;
    const resource = `Recordings/${body.RecordingSid}`;
    const resourcePaths = [path + resource, path + `Calls/${body.CallSid}/` + resource];
    if (
      !['https:', 'http:'].includes(url.protocol) ||
      !/^api(?:\.[a-z0-9-]+){0,2}\.twilio\.com$/.test(url.hostname) ||
      url.username ||
      url.password ||
      url.port ||
      url.search ||
      url.hash ||
      !resourcePaths.some((base) => [base, base + '.wav', base + '.mp3'].includes(url.pathname))
    )
      return null;
  }
  if (
    body.RecordingStatus === 'completed' &&
    (recordingUrl === null || durationSeconds === null || channels === null)
  )
    return null;
  return {
    accountSid: body.AccountSid,
    providerCallId: body.CallSid,
    recordingSid: body.RecordingSid,
    recordingStatus: body.RecordingStatus,
    recordingUrl,
    durationSeconds,
    channels,
    recordingStartTime,
    recordingSource,
    recordingTrack
  };
}
