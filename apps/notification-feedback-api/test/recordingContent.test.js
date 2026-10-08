import { describe, it, beforeEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import axios from 'axios';
import express from 'express';
import request from 'supertest';
import { Readable, PassThrough } from 'node:stream';
import { createServer, get } from 'node:http';
import { once } from 'node:events';
import sql from 'mssql';
import { mountRecordingContentRoute } from '../app/routes/recordingContent.js';
import requireBearerToken from '../app/middleware/requireBearerToken.js';

describe('Trusted recording content', () => {
  let app, repo, httpClient, config, recording, media;
  const path = '/api/v1/auto-call/recordings/7/content';
  it('bounds connection/header setup and returns 504 instead of treating timeout as caller cancellation', async () => {
    const clock = sinon.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    let signal;
    let started;
    const ready = new Promise((resolve) => {
      started = resolve;
    });
    httpClient.request.callsFake((options) => {
      signal = options.signal;
      started();
      return new Promise((_resolve, reject) => {
        signal.addEventListener(
          'abort',
          () => reject(Object.assign(new Error('canceled'), { code: 'ERR_CANCELED' })),
          { once: true }
        );
      });
    });
    try {
      const response = request(app)
        .get(path)
        .expect(504)
        .then((result) => result);
      await ready;
      await clock.tickAsync(15000);
      expect((await response).headers['cache-control']).to.equal('no-store');
      expect(signal.aborted).to.equal(true);
    } finally {
      clock.restore();
    }
  });
  it('streams through the real Axios HTTP transport with server-held credentials', async () => {
    let receivedHeaders;
    const upstreamServer = createServer((upstreamRequest, upstreamResponse) => {
      receivedHeaders = upstreamRequest.headers;
      upstreamResponse.writeHead(206, {
        'Content-Type': 'audio/mpeg',
        'Content-Length': '5',
        'Content-Range': 'bytes 0-4/20',
        'Accept-Ranges': 'bytes',
        'Set-Cookie': 'upstream-secret=hidden'
      });
      upstreamResponse.end('audio');
    });
    upstreamServer.listen(0, '127.0.0.1');
    await once(upstreamServer, 'listening');
    try {
      httpClient.request.callsFake((options) =>
        axios.request({
          ...options,
          url: `http://127.0.0.1:${upstreamServer.address().port}/recording.mp3`
        })
      );
      const response = await request(app)
        .get(path)
        .set('Range', 'bytes=0-4')
        .set('Authorization', 'Bearer ui-only-credential')
        .expect(206);
      expect(response.body.toString()).to.equal('audio');
      expect(receivedHeaders.authorization).to.equal(
        'Basic ' + Buffer.from(`${config.accountSid}:${config.authToken}`).toString('base64')
      );
      expect(receivedHeaders.range).to.equal('bytes=0-4');
      expect(receivedHeaders['accept-encoding']).to.equal('identity');
      expect(response.headers).not.to.have.property('set-cookie');
      expect(response.headers['content-range']).to.equal('bytes 0-4/20');
    } finally {
      upstreamServer.closeAllConnections();
      await new Promise((resolve) => upstreamServer.close(resolve));
    }
  });
  beforeEach(() => {
    config = {
      accountSid: 'AC' + 'a'.repeat(32),
      authToken: 'test-secret',
      database: 'AutoCallDB'
    };
    recording = {
      Provider: 'Twilio',
      AccountSid: config.accountSid,
      ProviderCallId: 'CA' + 'b'.repeat(32),
      RecordingSid: 'RE' + 'c'.repeat(32),
      RecordingStatus: 'completed',
      RecordingUrl: `http://api.twilio.com/2010-04-01/Accounts/${config.accountSid}/Recordings/RE${'c'.repeat(32)}`
    };
    media = {
      status: 200,
      headers: { 'content-type': 'audio/mpeg', 'content-length': '5', 'accept-ranges': 'bytes' },
      data: Readable.from([Buffer.from('audio')])
    };
    repo = { getAutomatedCallRecording: sinon.stub().resolves(recording) };
    httpClient = { request: sinon.stub().resolves(media) };
    app = express();
    mountRecordingContentRoute(app, { repo, httpClient, getConfig: () => config });
    app.use(requireBearerToken());
    app.get('/private', (_req, res) => res.sendStatus(200));
  });
  it('streams audio without a playback token or bearer credential', async () => {
    const response = await request(app).get(path).expect(200);
    expect(response.body.toString()).to.equal('audio');
    expect(response.headers['cache-control']).to.equal('no-store');
    expect(repo.getAutomatedCallRecording.firstCall.args).to.deep.equal([
      { recordingId: 7 },
      { platform: 'AutoCallDB', timeoutMs: 5000 }
    ]);
    const options = httpClient.request.firstCall.args[0];
    expect(options.url).to.equal(recording.RecordingUrl.replace('http:', 'https:') + '.mp3');
    expect(options.auth).to.deep.equal({ username: config.accountSid, password: 'test-secret' });
    expect(options.maxRedirects).to.equal(0);
    expect(response.headers).not.to.have.property('authorization');
  });
  it('forwards a byte range and preserves partial media headers', async () => {
    media.status = 206;
    media.headers['content-range'] = 'bytes 0-4/20';
    const response = await request(app).get(path).set('Range', 'bytes=0-4').expect(206);
    expect(response.body.toString()).to.equal('audio');
    expect(response.headers['content-range']).to.equal('bytes 0-4/20');
    expect(httpClient.request.firstCall.args[0].headers.Range).to.equal('bytes=0-4');
  });
  it('rejects an untrusted stored URL without sending credentials', async () => {
    recording.RecordingUrl = 'https://attacker.example/audio.mp3';
    await request(app).get(path).expect(502);
    expect(httpClient.request.called).to.equal(false);
  });
  it('keeps bearer authentication on unrelated routes and methods', async () => {
    await request(app).get('/private').expect(401);
    await request(app).post(path).expect(401);
    await request(app)
      .get(path + '/other')
      .expect(401);
  });
  for (const recordingId of ['0', '-1', '2147483648', '1.5', '07', 'RE' + 'c'.repeat(32)]) {
    it(`rejects invalid recording ID ${recordingId} before SQL`, async () => {
      await request(app)
        .get(path.replace('/7/', `/${recordingId}/`))
        .expect(400);
      expect(repo.getAutomatedCallRecording.called).to.equal(false);
      expect(httpClient.request.called).to.equal(false);
    });
  }
  it('rejects unsupported ranges before SQL', async () => {
    for (const range of ['items=1-5', 'bytes=0-1,3-4', 'bytes=-', 'bytes=a-b']) {
      await request(app).get(path).set('Range', range).expect(400);
    }
    expect(repo.getAutomatedCallRecording.called).to.equal(false);
  });
  it('supports open-ended and suffix ranges when upstream returns the full audio', async () => {
    for (const range of ['bytes=2-', 'bytes=-2']) {
      httpClient.request.resolves({ ...media, data: Readable.from([Buffer.from('audio')]) });
      await request(app).get(path).set('Range', range).expect(200);
      expect(httpClient.request.lastCall.args[0].headers.Range).to.equal(range);
    }
  });
  it('returns 404 for unknown recordings without requesting Twilio', async () => {
    repo.getAutomatedCallRecording.resolves(null);
    await request(app).get(path).expect(404);
    expect(httpClient.request.called).to.equal(false);
  });
  for (const status of ['in-progress', 'absent', 'failed']) {
    it(`does not retrieve ${status} recording media`, async () => {
      recording.RecordingStatus = status;
      await request(app).get(path).expect(409);
      expect(httpClient.request.called).to.equal(false);
    });
  }
  it('rejects missing media locations', async () => {
    recording.RecordingUrl = null;
    await request(app).get(path).expect(409);
    expect(httpClient.request.called).to.equal(false);
  });
  for (const [field, value] of [
    ['accountSid', undefined],
    ['authToken', ''],
    ['database', '']
  ]) {
    it(`fails closed for missing ${field} configuration`, async () => {
      config[field] = value;
      await request(app).get(path).expect(503);
      expect(repo.getAutomatedCallRecording.called).to.equal(false);
    });
  }
  for (const [field, value] of [
    ['Provider', 'Other'],
    ['AccountSid', 'AC' + 'd'.repeat(32)],
    ['RecordingSid', 'bad'],
    ['ProviderCallId', 'bad'],
    ['RecordingUrl', 'https://api.twilio.com.evil.example/audio.mp3'],
    ['RecordingUrl', 'https://localhost/audio.mp3']
  ]) {
    it(`rejects mismatched or unsafe stored ${field}: ${value}`, async () => {
      recording[field] = value;
      await request(app).get(path).expect(502);
      expect(httpClient.request.called).to.equal(false);
    });
  }
  it('uses the stored regional host and normalizes call-scoped WAV locations to MP3', async () => {
    recording.RecordingUrl =
      recording.RecordingUrl.replace('api.twilio.com', 'api.sydney.au1.twilio.com').replace(
        '/Recordings/',
        `/Calls/${recording.ProviderCallId}/Recordings/`
      ) + '.wav';
    await request(app).get(path).expect(200);
    expect(httpClient.request.firstCall.args[0].url).to.equal(
      recording.RecordingUrl.replace('http:', 'https:').replace('.wav', '.mp3')
    );
  });
  it('returns audio headers without a body for HEAD', async () => {
    const response = await request(app).head(path).expect(200);
    expect(response.headers['content-type']).to.equal('audio/mpeg');
    expect(response.headers['content-length']).to.equal('5');
    expect(response.text).to.equal(undefined);
    expect(httpClient.request.firstCall.args[0].method).to.equal('HEAD');
    expect(media.data.destroyed).to.equal(true);
  });
  for (const [upstreamStatus, expectedStatus] of [
    [302, 502],
    [401, 502],
    [403, 502],
    [404, 410],
    [410, 410],
    [429, 503],
    [500, 502],
    [503, 503]
  ]) {
    it(`maps upstream ${upstreamStatus} without forwarding response bodies or credentials`, async () => {
      media.status = upstreamStatus;
      media.headers.location = 'https://attacker.example/';
      media.headers['set-cookie'] = 'secret-cookie';
      const response = await request(app).get(path).expect(expectedStatus);
      expect(response.headers).not.to.have.property('location');
      expect(response.headers).not.to.have.property('set-cookie');
      expect(response.headers['cache-control']).to.equal('no-store');
      expect(response.text).not.to.include('test-secret');
      expect(media.data.destroyed).to.equal(true);
      expect(httpClient.request.calledOnce).to.equal(true);
    });
  }
  it('preserves an unsatisfiable range response without relaying its upstream body', async () => {
    media.status = 416;
    media.headers['content-range'] = 'bytes */5';
    const response = await request(app).get(path).set('Range', 'bytes=99-').expect(416);
    expect(response.headers['content-range']).to.equal('bytes */5');
    expect(response.text).not.to.equal('audio');
  });
  for (const [field, value] of [
    ['content-type', 'application/json'],
    ['content-length', '-5'],
    ['content-encoding', 'gzip']
  ]) {
    it(`rejects invalid upstream ${field}`, async () => {
      media.headers[field] = value;
      await request(app).get(path).expect(502);
      expect(media.data.destroyed).to.equal(true);
    });
  }
  for (const contentRange of [undefined, 'bytes 4-0/20', 'bytes 0-4/4', 'bytes 0-3/20']) {
    it(`rejects inconsistent partial media headers: ${contentRange}`, async () => {
      media.status = 206;
      media.headers['content-range'] = contentRange;
      await request(app).get(path).set('Range', 'bytes=0-4').expect(502);
    });
  }
  it('masks lookup failures and does not contact Twilio', async () => {
    repo.getAutomatedCallRecording.rejects(new Error('secret SQL details'));
    const response = await request(app).get(path).expect(500);
    expect(response.text).not.to.include('secret SQL details');
    expect(httpClient.request.called).to.equal(false);
  });
  for (const [code, expectedStatus] of [
    ['ECONNABORTED', 504],
    ['ETIMEDOUT', 504],
    ['ECONNREFUSED', 502]
  ]) {
    it(`masks network errors and maps ${code}`, async () => {
      httpClient.request.rejects(Object.assign(new Error('test-secret'), { code }));
      const response = await request(app).get(path).expect(expectedStatus);
      expect(response.text).not.to.include('test-secret');
    });
  }
  it('streams the first chunk before completion and cancels upstream when the caller disconnects', async () => {
    media.data = new PassThrough();
    delete media.headers['content-length'];
    const closed = new Promise((resolve) => media.data.once('close', resolve));
    let signal;
    const aborted = new Promise((resolve) => {
      httpClient.request.callsFake(async (options) => {
        signal = options.signal;
        signal.addEventListener('abort', resolve, { once: true });
        media.data.write('audio');
        return media;
      });
    });
    const server = createServer(app);
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    let client;
    try {
      await new Promise((resolve, reject) => {
        client = get(`http://127.0.0.1:${server.address().port}${path}`, (response) => {
          response.once('data', (chunk) => {
            try {
              expect(chunk.toString()).to.equal('audio');
              expect(media.data.writableEnded).to.equal(false);
              response.destroy();
              client.destroy();
              resolve();
            } catch (error) {
              reject(error);
            }
          });
          response.once('error', reject);
        });
        client.once('error', reject);
      });
      await aborted;
      await closed;
      expect(signal.aborted).to.equal(true);
      expect(media.data.destroyed).to.equal(true);
    } finally {
      client?.destroy();
      media.data.destroy();
      server.closeAllConnections();
      await new Promise((resolve) => server.close(resolve));
    }
  });
  it('binds the recording lookup as a typed SQL parameter on AutoCallDB', async () => {
    const { initialize } = await import('../../../pkg/repository/sqlserver.js');
    const { getAutomatedCallRecording } =
      await import('../../../pkg/repository/automatedCallRepository.js');
    const pools = await initialize();
    const original = pools.AUTOCALLDB;
    const fakeRequest = {
      input: sinon.stub(),
      execute: sinon.stub().resolves({ recordset: [recording] })
    };
    try {
      pools.AUTOCALLDB = Promise.resolve({ request: () => fakeRequest });
      expect(await getAutomatedCallRecording({ recordingId: 7 }, { timeoutMs: 5000 })).to.equal(
        recording
      );
      expect(fakeRequest.input.calledOnceWithExactly('RecordingId', sql.Int, 7)).to.equal(true);
      expect(fakeRequest.execute.calledOnceWithExactly('dbo.automatedcalls_GetRecording')).to.equal(
        true
      );
      expect(fakeRequest.timeout).to.equal(5000);
      fakeRequest.execute.resolves({ recordset: [] });
      expect(await getAutomatedCallRecording({ recordingId: 7 })).to.equal(null);
      for (const recordingId of [0, -1, 2147483648, 1.5, '7']) {
        let error;
        try {
          await getAutomatedCallRecording({ recordingId });
        } catch (caught) {
          error = caught;
        }
        expect(error).to.be.instanceOf(TypeError);
      }
    } finally {
      if (original === undefined) delete pools.AUTOCALLDB;
      else pools.AUTOCALLDB = original;
    }
  });
});
