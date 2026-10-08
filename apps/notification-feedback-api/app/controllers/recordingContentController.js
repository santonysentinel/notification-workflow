import axios from 'axios';
import nconf from 'nconf';
import { pipeline } from 'node:stream/promises';
import * as repository from '../../../../pkg/repository/automatedCallRepository.js';
import { sendError } from '../../configure/errors.js';
import { parseRecordingCallback } from '../utils/twilioRecordings.js';

export function readRecordingContentConfig() {
  return {
    accountSid: process.env.TWILIO_ACCOUNT_SID ?? nconf.get('twilio:accountSid'),
    authToken: process.env.TWILIO_AUTH_TOKEN ?? nconf.get('twilio:authToken'),
    database: process.env.AUTO_CALL_DATABASE ?? nconf.get('twilio:database') ?? 'AutoCallDB'
  };
}

export function createRecordingContentController({
  repo = repository,
  httpClient = axios,
  getConfig = readRecordingContentConfig
} = {}) {
  return async (req, res) => {
    res.set('Cache-Control', 'no-store');
    const context = { traceId: req.context?.traceId, path: req.originalUrl };
    const recordingId = /^[1-9]\d*$/.test(req.params.recordingId)
      ? Number(req.params.recordingId)
      : NaN;
    if (!Number.isInteger(recordingId) || recordingId > 2147483647) {
      return sendError(res, 400, 'recordingId must be a positive SQL integer', context);
    }
    const range = req.get('Range');
    if (range && !/^bytes=(?:\d+-\d*|-\d+)$/.test(range)) {
      return sendError(res, 400, 'A single byte range is required', context);
    }
    const config = getConfig();
    if (
      !/^AC[0-9a-fA-F]{32}$/.test(config.accountSid ?? '') ||
      typeof config.authToken !== 'string' ||
      !config.authToken.trim() ||
      typeof config.database !== 'string' ||
      !config.database.trim()
    ) {
      return sendError(res, 503, 'Recording playback is not configured', context);
    }
    const cancellation = new AbortController();
    const abort = () => cancellation.abort();
    const onClose = () => {
      if (!res.writableFinished) abort();
    };
    req.once('aborted', abort);
    res.once('close', onClose);
    let upstream;
    let headerDeadline;
    let headersTimedOut = false;
    let phase = 'lookup';
    try {
      const recording = await repo.getAutomatedCallRecording(
        { recordingId },
        {
          platform: config.database,
          timeoutMs: 5000
        }
      );
      if (cancellation.signal.aborted || res.destroyed) return;
      if (!recording) return sendError(res, 404, 'Recording not found', context);
      if (recording.RecordingStatus !== 'completed' || !recording.RecordingUrl) {
        return sendError(res, 409, 'Recording is not ready for playback', context);
      }
      const parsed = parseRecordingCallback({
        AccountSid: recording.AccountSid,
        CallSid: recording.ProviderCallId,
        RecordingSid: recording.RecordingSid,
        RecordingStatus: 'completed',
        RecordingUrl: recording.RecordingUrl,
        RecordingDuration: '0',
        RecordingChannels: '1'
      });
      if (
        recording.Provider !== 'Twilio' ||
        recording.AccountSid !== config.accountSid ||
        !parsed
      ) {
        return sendError(res, 502, 'Recording media location is invalid', context);
      }
      const url = new URL(parsed.recordingUrl);
      url.protocol = 'https:';
      url.pathname = url.pathname.replace(/\.(wav|mp3)$/, '') + '.mp3';
      phase = 'upstream';
      headerDeadline = setTimeout(() => {
        headersTimedOut = true;
        abort();
      }, 15000);
      headerDeadline.unref?.();
      upstream = await httpClient.request({
        method: req.method === 'HEAD' ? 'HEAD' : 'GET',
        url: url.href,
        auth: { username: config.accountSid, password: config.authToken },
        responseType: 'stream',
        timeout: 15000,
        signal: cancellation.signal,
        maxRedirects: 0,
        proxy: false,
        decompress: false,
        headers: {
          Accept: 'audio/mpeg',
          'Accept-Encoding': 'identity',
          ...(range ? { Range: range } : {})
        },
        validateStatus: () => true
      });
      clearTimeout(headerDeadline);
      if (headersTimedOut) return sendError(res, 504, 'Recording media request timed out', context);
      if (cancellation.signal.aborted || res.destroyed) return;
      if (upstream.status === 404 || upstream.status === 410) {
        return sendError(res, 410, 'Recording media is no longer available', context);
      }
      if (upstream.status === 416) {
        const contentRange = upstream.headers['content-range'];
        if (/^bytes \*\/\d+$/.test(contentRange ?? '')) res.set('Content-Range', contentRange);
        return sendError(res, 416, 'Requested byte range cannot be satisfied', context);
      }
      if (![200, 206].includes(upstream.status)) {
        return sendError(
          res,
          upstream.status === 429 || upstream.status === 503 ? 503 : 502,
          'Recording media could not be retrieved',
          context
        );
      }
      const contentType = upstream.headers['content-type'];
      const contentLength = upstream.headers['content-length'];
      const contentRange = upstream.headers['content-range'];
      const encoding = upstream.headers['content-encoding'];
      const partial = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(contentRange ?? '');
      if (
        !/^audio\/(mpeg|mp3)(?:\s*;|$)/i.test(contentType ?? '') ||
        (encoding && encoding !== 'identity') ||
        (contentLength !== undefined && !/^\d+$/.test(contentLength)) ||
        (upstream.status === 206 &&
          (!range ||
            !partial ||
            BigInt(partial[1]) > BigInt(partial[2]) ||
            BigInt(partial[2]) >= BigInt(partial[3]) ||
            (contentLength !== undefined &&
              BigInt(contentLength) !== BigInt(partial[2]) - BigInt(partial[1]) + 1n)))
      ) {
        return sendError(res, 502, 'Recording media response is invalid', context);
      }
      res
        .status(upstream.status)
        .set('Content-Type', 'audio/mpeg')
        .set('X-Content-Type-Options', 'nosniff');
      if (contentLength !== undefined) res.set('Content-Length', contentLength);
      if (upstream.status === 206) res.set('Content-Range', contentRange);
      if (['bytes', 'none'].includes(upstream.headers['accept-ranges'])) {
        res.set('Accept-Ranges', upstream.headers['accept-ranges']);
      }
      if (req.method === 'HEAD') return res.end();
      phase = 'stream';
      await pipeline(upstream.data, res, { signal: cancellation.signal });
    } catch (err) {
      if ((cancellation.signal.aborted && !headersTimedOut) || res.destroyed) return;
      req.log?.error('recording.content.failed', { recordingId, phase });
      if (res.headersSent) {
        res.destroy();
        return;
      }
      res.removeHeader('Content-Length');
      res.removeHeader('Content-Range');
      res.removeHeader('Accept-Ranges');
      const timeout = headersTimedOut || ['ECONNABORTED', 'ETIMEDOUT'].includes(err?.code);
      return sendError(
        res,
        phase === 'lookup' ? 500 : timeout ? 504 : 502,
        'Recording content could not be retrieved',
        context
      );
    } finally {
      clearTimeout(headerDeadline);
      upstream?.data?.destroy();
      req.off('aborted', abort);
      res.off('close', onClose);
    }
  };
}
