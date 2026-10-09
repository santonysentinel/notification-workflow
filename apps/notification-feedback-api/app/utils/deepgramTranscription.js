import { createHash } from 'node:crypto';
import { canonicalize } from 'json-canonicalize';
import { correlationPattern } from '../middleware/requireDeepgramCallbackJwt.js';

const object = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const seconds = (value) =>
  typeof value === 'number' &&
  Number.isFinite(value) &&
  value >= 0 &&
  Math.round(value * 1000) <= 2147483647;
const confidence = (value) =>
  value == null ||
  (typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1);

export function normalizeDeepgramCallback(body, context) {
  if (!object(body)) return null;
  const requestId = body.metadata?.request_id ?? body.request_id;
  if (
    typeof requestId !== 'string' ||
    !correlationPattern.test(requestId) ||
    (body.request_id !== undefined && body.request_id !== requestId)
  )
    return null;
  let payloadHash;
  try {
    payloadHash = createHash('sha256').update(canonicalize(body)).digest();
  } catch {
    return null;
  }
  const result = {
    requestId: requestId.toLowerCase(),
    payloadHash,
    response: body,
    transcripts: []
  };
  if (body.err_code !== undefined || body.err_msg !== undefined) {
    if (
      typeof body.err_code !== 'string' ||
      !/^[A-Z0-9_]{1,100}$/.test(body.err_code) ||
      typeof body.err_msg !== 'string' ||
      !body.err_msg ||
      body.results !== undefined
    )
      return null;
    return {
      ...result,
      success: false,
      errorCode: body.err_code,
      errorMessage: 'Deepgram reported a transcription error'
    };
  }
  let configuration;
  try {
    configuration = JSON.parse(context.ConfigurationJSON);
  } catch {
    return null;
  }
  const count = body.metadata?.channels;
  const duration = body.metadata?.duration;
  const channels = body.results?.channels;
  if (
    !object(configuration) ||
    ![1, 2].includes(count) ||
    count !== context.Channels ||
    !seconds(duration) ||
    !Array.isArray(channels) ||
    channels.length !== (configuration.multichannel === true ? count : 1)
  )
    return null;
  let speech = false;
  for (const channel of channels) {
    const alternative = channel?.alternatives?.[0];
    if (!object(alternative) || typeof alternative.transcript !== 'string') return null;
    speech ||= alternative.transcript.trim().length > 0;
  }
  const utterances = body.results.utterances ?? [];
  if (
    !Array.isArray(utterances) ||
    utterances.length > 20000 ||
    (speech && utterances.length === 0)
  )
    return null;
  for (const [index, utterance] of utterances.entries()) {
    if (
      !object(utterance) ||
      !seconds(utterance.start) ||
      !seconds(utterance.end) ||
      utterance.end < utterance.start ||
      utterance.end > duration + 0.01 ||
      !Number.isInteger(utterance.channel) ||
      utterance.channel < 0 ||
      utterance.channel >= channels.length ||
      typeof utterance.transcript !== 'string' ||
      !confidence(utterance.confidence) ||
      !Array.isArray(utterance.words)
    )
      return null;
    for (const word of utterance.words) {
      if (
        !object(word) ||
        typeof word.word !== 'string' ||
        !seconds(word.start) ||
        !seconds(word.end) ||
        word.end < word.start ||
        word.start < utterance.start - 0.01 ||
        word.end > utterance.end + 0.01 ||
        !confidence(word.confidence)
      )
        return null;
    }
    const language =
      channels[utterance.channel]?.detected_language ?? configuration.language ?? null;
    const model = configuration.model ?? null;
    if (
      (language !== null && (typeof language !== 'string' || language.length > 35)) ||
      (model !== null && (typeof model !== 'string' || model.length > 200))
    )
      return null;
    result.transcripts.push({
      sentence: index,
      channel: utterance.channel,
      startTimeMilliseconds: Math.round(utterance.start * 1000),
      endTimeMilliseconds: Math.round(utterance.end * 1000),
      transcriptText: utterance.transcript,
      confidence: utterance.confidence ?? null,
      wordsJSON: JSON.stringify(utterance.words),
      providerModel: model,
      language
    });
  }
  return { ...result, success: true, errorCode: null, errorMessage: null };
}
