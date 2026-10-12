import { before, beforeEach, describe, it } from 'mocha';
import { expect } from 'chai';
import express from 'express';
import request from 'supertest';
import { generateKeyPair, exportSPKI, exportJWK, SignJWT } from 'jose';
import requireDeepgramCallbackJwt from '../app/middleware/requireDeepgramCallbackJwt.js';
import { normalizeDeepgramCallback } from '../app/utils/deepgramTranscription.js';
import { createHash } from 'node:crypto';
import sinon from 'sinon';
import { createDeepgramTranscriptionsRouter } from '../app/routes/deepgramTranscriptions.js';
import requireBearerToken from '../app/middleware/requireBearerToken.js';
import * as callbackRepository from '../../../pkg/repository/automatedCallRepository.js';
import { initialize } from '../../../pkg/repository/sqlserver.js';

describe('Deepgram callback JWT', () => {
  let keys, config, app;
  const correlationId = '11111111-1111-4111-8111-111111111111';
  before(async () => {
    keys = await generateKeyPair('RS256');
  });
  beforeEach(async () => {
    config = {
      issuer: 'https://identity.example',
      audience: 'deepgram-callback',
      permissionId: '1188',
      algorithm: 'RS256',
      publicKey: await exportSPKI(keys.publicKey),
      database: 'AutoCallDB'
    };
    app = express();
    app.post('/callback', requireDeepgramCallbackJwt({ getConfig: () => config }), (req, res) =>
      res.json({
        correlationId: req.deepgramCallback.correlationId,
        tokenHash: req.deepgramCallback.tokenHash.toString('hex')
      })
    );
  });
  async function token(claims = {}, options = {}) {
    return new SignJWT({
      permissions: '12,1188',
      ...claims
    })
      .setProtectedHeader({ alg: 'RS256' })
      .setIssuer(options.issuer ?? config.issuer)
      .setAudience(options.audience ?? config.audience)
      .setExpirationTime(options.expiry ?? '1h')
      .sign(keys.privateKey);
  }
  async function send(jwt, username = correlationId) {
    return request(app)
      .post('/callback')
      .set('Authorization', 'Basic ' + Buffer.from(username + ':' + jwt).toString('base64'));
  }
  it('accepts a permission-bearing asymmetric JWT without custom identity claims', async () => {
    const jwt = await token();
    const response = await send(jwt);
    expect(response.status).to.equal(200);
    expect(response.body).to.deep.equal({
      correlationId,
      tokenHash: createHash('sha256').update(jwt).digest('hex')
    });
  });
  for (const claims of [
    { permissions: '11188' },
    { permissions: ['1188'] },
    { permissions: '' },
    { permissions: '1188,' },
    { permissions: 'bad,1188' }
  ]) {
    it(`rejects invalid claims ${JSON.stringify(claims)}`, async () => {
      expect((await send(await token(claims))).status).to.equal(401);
    });
  }
  for (const options of [
    { issuer: 'https://other.example' },
    { audience: 'other' },
    { expiry: 1 }
  ]) {
    it(`rejects invalid trust/expiry ${JSON.stringify(options)}`, async () => {
      expect((await send(await token({}, options))).status).to.equal(401);
    });
  }
  it('rejects an invalid Basic username and missing authentication', async () => {
    expect((await send(await token(), 'bad-correlation')).status).to.equal(401);
    await request(app).post('/callback').expect(401);
  });
  it('requires an explicitly configured complete permission ID and accepts trimmed CSV entries', async () => {
    const jwt = await token({ permissions: '12, 1188 ,13' });
    expect((await send(jwt)).status).to.equal(200);
    for (const permissionId of [undefined, '', '0', '01188', '1188,12', ' 1188']) {
      config.permissionId = permissionId;
      expect((await send(jwt)).status).to.equal(503);
    }
  });
  it('allows HTTP JWKS only at the exact explicitly configured identity endpoint', async () => {
    const jwt = await token();
    const jwk = await exportJWK(keys.publicKey);
    const fetchStub = sinon
      .stub(globalThis, 'fetch')
      .callsFake(async () => new Response(JSON.stringify({ keys: [jwk] }), { status: 200 }));
    config.publicKey = undefined;
    config.identityBaseUrl = 'http://172.27.112.243:8085';
    config.jwtJwksPath = '/.well-known/jwks.json';
    try {
      expect((await send(jwt)).status).to.equal(503);
      expect(fetchStub.callCount).to.equal(0);
      config.allowInsecureIdentityHttp = true;
      expect((await send(jwt)).status).to.equal(200);
      expect(String(fetchStub.firstCall.args[0])).to.equal(
        config.identityBaseUrl + config.jwtJwksPath
      );
      expect(fetchStub.firstCall.args[1].redirect).to.equal('error');
      for (const jwksUrl of [
        'http://172.27.112.243:8086/.well-known/jwks.json',
        'http://other.example:8085/.well-known/jwks.json',
        'http://172.27.112.243:8085/other-keys'
      ]) {
        config.jwksUrl = jwksUrl;
        expect((await send(jwt)).status).to.equal(503);
      }
      expect(fetchStub.callCount).to.equal(1);
    } finally {
      fetchStub.restore();
    }
  });
  it('bounds unknown-length JWKS responses and masks redirect failures', async () => {
    const jwt = await token();
    config.publicKey = undefined;
    config.jwksUrl = 'https://identity.example/oversized-keys';
    const fetchStub = sinon
      .stub(globalThis, 'fetch')
      .callsFake(async () => new Response('x'.repeat(65537), { status: 200 }));
    try {
      const oversized = await send(jwt);
      expect(oversized.status).to.equal(503);
      expect(JSON.stringify(oversized.body)).not.to.include(jwt);
      config.jwksUrl = 'https://identity.example/redirected-keys';
      fetchStub.rejects(new TypeError('credentials and upstream URL must be masked'));
      const redirected = await send(jwt);
      expect(redirected.status).to.equal(503);
      expect(JSON.stringify(redirected.body)).not.to.include('upstream URL');
    } finally {
      fetchStub.restore();
    }
  });
  it('fails closed for missing keys and unsupported algorithms', async () => {
    const jwt = await token();
    config.publicKey = undefined;
    await send(jwt).then((response) => expect(response.status).to.equal(503));
    config.publicKey = await exportSPKI(keys.publicKey);
    config.algorithm = 'HS256';
    await send(jwt).then((response) => expect(response.status).to.equal(503));
  });
  it('rejects a different signing key, a symmetric token, missing expiry and a future not-before', async () => {
    const claims = {
      permissions: config.permissionId
    };
    const otherKeys = await generateKeyPair('RS256');
    const signed = (privateKey) =>
      new SignJWT(claims)
        .setProtectedHeader({ alg: 'RS256' })
        .setIssuer(config.issuer)
        .setAudience(config.audience)
        .setExpirationTime('1h')
        .sign(privateKey);
    expect((await send(await signed(otherKeys.privateKey))).status).to.equal(401);
    const symmetric = await new SignJWT(claims)
      .setProtectedHeader({ alg: 'HS256' })
      .setIssuer(config.issuer)
      .setAudience(config.audience)
      .setExpirationTime('1h')
      .sign(new TextEncoder().encode('test-only-secret'));
    expect((await send(symmetric)).status).to.equal(401);
    const noExpiry = await new SignJWT(claims)
      .setProtectedHeader({ alg: 'RS256' })
      .setIssuer(config.issuer)
      .setAudience(config.audience)
      .sign(keys.privateKey);
    expect((await send(noExpiry)).status).to.equal(401);
    expect(
      (await send(await token({ nbf: Math.floor(Date.now() / 1000) + 3600 }))).status
    ).to.equal(401);
  });
  it('supports explicitly configured ES256 and fails closed on an unsafe JWKS URL', async () => {
    const ecKeys = await generateKeyPair('ES256');
    config.algorithm = 'ES256';
    config.publicKey = await exportSPKI(ecKeys.publicKey);
    const jwt = await new SignJWT({
      permissions: config.permissionId
    })
      .setProtectedHeader({ alg: 'ES256' })
      .setIssuer(config.issuer)
      .setAudience(config.audience)
      .setExpirationTime('1h')
      .sign(ecKeys.privateKey);
    expect((await send(jwt)).status).to.equal(200);
    config.publicKey = undefined;
    config.jwksUrl = 'http://identity.example/keys';
    expect((await send(jwt)).status).to.equal(503);
  });
  it('verifies pinned HTTPS JWKS keys, caches them, and masks unavailable or invalid key responses', async () => {
    const jwt = await token();
    const jwk = await exportJWK(keys.publicKey);
    const fetchStub = sinon
      .stub(globalThis, 'fetch')
      .resolves(new Response(JSON.stringify({ keys: [jwk] }), { status: 200 }));
    config.publicKey = undefined;
    config.jwksUrl = 'https://identity.example/callback-test-keys';
    try {
      expect((await send(jwt)).status).to.equal(200);
      expect((await send(jwt)).status).to.equal(200);
      expect(fetchStub.callCount).to.equal(1);
      expect(String(fetchStub.firstCall.args[0])).to.equal(config.jwksUrl);
      config.jwksUrl = 'https://identity.example/callback-test-unavailable';
      fetchStub.resolves(new Response('upstream unavailable', { status: 503 }));
      expect((await send(jwt)).status).to.equal(503);
      config.jwksUrl = 'https://identity.example/callback-test-invalid-keys';
      fetchStub.resolves(new Response(JSON.stringify({ keys: null }), { status: 200 }));
      expect((await send(jwt)).status).to.equal(503);
    } finally {
      fetchStub.restore();
    }
  });
  it('persists a signed JSON callback through the public router and keeps unrelated routes protected', async () => {
    const jwt = await token();
    const repo = {
      getDeepgramCallbackContext: sinon.stub().resolves({
        RecordingId: 7,
        CallbackTokenHash: createHash('sha256').update(jwt).digest(),
        Channels: 1,
        ConfigurationJSON: '{"model":"nova-3"}'
      }),
      completeDeepgramCallback: sinon.stub().resolves({ Outcome: 'recorded', Applied: true })
    };
    app = express();
    app.use(
      '/webhooks/deepgram/transcriptions',
      createDeepgramTranscriptionsRouter({ repository: repo, getConfig: () => config })
    );
    app.use(requireBearerToken());
    app.get('/private', (_req, res) => res.sendStatus(200));
    const path = '/webhooks/deepgram/transcriptions/completed';
    const body = {
      metadata: { request_id: '33333333-3333-4333-8333-333333333333', channels: 1, duration: 2 },
      results: { channels: [{ alternatives: [{ transcript: '' }] }], utterances: [] }
    };
    const authorization = 'Basic ' + Buffer.from(correlationId + ':' + jwt).toString('base64');
    const callback = (payload) =>
      request(app).post(path).set('Authorization', authorization).send(payload);
    const response = await callback(body).expect(204);
    expect(response.headers['cache-control']).to.equal('no-store');
    expect(repo.completeDeepgramCallback.firstCall.args[0]).to.include({
      recordingId: 7,
      correlationId,
      success: true
    });
    expect(repo.completeDeepgramCallback.firstCall.args[0].transcripts).to.deep.equal([]);
    await callback({}).expect(400);
    repo.getDeepgramCallbackContext.resolves({
      RecordingId: 7,
      CallbackTokenHash: Buffer.alloc(32),
      Channels: 1,
      ConfigurationJSON: '{}'
    });
    await callback(body).expect(401);
    repo.getDeepgramCallbackContext.resolves({
      RecordingId: 8,
      CallbackTokenHash: createHash('sha256').update(jwt).digest(),
      Channels: 1,
      ConfigurationJSON: '{}'
    });
    await callback(body).expect(204);
    expect(repo.completeDeepgramCallback.lastCall.args[0].recordingId).to.equal(8);
    repo.getDeepgramCallbackContext.resolves({
      RecordingId: 0,
      CallbackTokenHash: createHash('sha256').update(jwt).digest()
    });
    await callback(body).expect(401);
    repo.getDeepgramCallbackContext.resolves({
      RecordingId: 7,
      CallbackTokenHash: createHash('sha256').update(jwt).digest(),
      Channels: 1,
      ConfigurationJSON: '{"model":"nova-3"}'
    });
    for (const [outcome, status] of [
      ['duplicate', 204],
      ['request-conflict', 409],
      ['payload-conflict', 409],
      ['unauthorized', 401],
      ['unexpected', 500]
    ]) {
      repo.completeDeepgramCallback.resolves({ Outcome: outcome });
      await callback(body).expect(status);
    }
    repo.getDeepgramCallbackContext.resolves(null);
    await callback(body).expect(401);
    await request(app).get('/private').expect(401);
    await request(app).post(path).send(body).expect(401);
    await request(app)
      .post(path)
      .set('Authorization', authorization)
      .type('form')
      .send('x=1')
      .expect(415);
    await request(app)
      .post(path)
      .set('Authorization', authorization)
      .set('Content-Type', 'application/json')
      .send('{ invalid secret')
      .expect(400);
    await request(app)
      .post(path)
      .set('Authorization', authorization)
      .send({ extra: 'x'.repeat(8 * 1024 * 1024) })
      .expect(413);
  });
});

describe('Deepgram callback repository', () => {
  it('freezes complete version-2 provider options when enqueueing a new recording', async () => {
    const pools = await initialize();
    const original = pools.AUTOCALLDB;
    const fakeRequest = {
      input: sinon.stub().returnsThis(),
      execute: sinon.stub().resolves({ recordset: [{ Outcome: 'recorded' }] })
    };
    pools.AUTOCALLDB = Promise.resolve({ request: () => fakeRequest });
    try {
      await callbackRepository.recordAutomatedCallRecordingStatus({
        callId: 1,
        providerCallId: 'CA' + '1'.repeat(32),
        accountSid: 'AC' + '2'.repeat(32),
        recordingSid: 'RE' + '3'.repeat(32),
        recordingStatus: 'completed',
        durationSeconds: 1,
        channels: 2,
        parameters: { model: 'untrusted', mip_opt_out: false }
      });
      const bindings = Object.fromEntries(
        fakeRequest.input.args.map(([name, , value]) => [name, value])
      );
      expect(bindings.TranscriptionConfigurationVersion).to.equal(2);
      expect(JSON.parse(bindings.TranscriptionConfigurationJSON)).to.deep.equal({
        model: 'nova-3',
        version: 'latest',
        language: 'en',
        utterances: true,
        multichannel: true,
        diarize: false,
        punctuate: false,
        smart_format: false,
        mip_opt_out: true
      });
      expect(fakeRequest.execute.firstCall.args[0]).to.equal('dbo.automatedcalls_RecordingStatus');
    } finally {
      if (original === undefined) delete pools.AUTOCALLDB;
      else pools.AUTOCALLDB = original;
    }
  });
  it('binds typed callback parameters to the strict configured SQL pool', async () => {
    const pools = await initialize();
    const original = pools.AUTOCALLDB;
    const fakeRequest = {
      input: sinon.stub().returnsThis(),
      execute: sinon.stub().resolves({ recordset: [{ Outcome: 'recorded' }] })
    };
    pools.AUTOCALLDB = Promise.resolve({ request: () => fakeRequest });
    const correlationId = '11111111-1111-4111-8111-111111111111';
    try {
      await callbackRepository.getDeepgramCallbackContext({ correlationId });
      expect(fakeRequest.execute.firstCall.args[0]).to.equal(
        'dbo.transcription_GetCallbackContext'
      );
      const data = {
        correlationId,
        recordingId: 7,
        tokenHash: Buffer.alloc(32),
        payloadHash: Buffer.alloc(32),
        requestId: '33333333-3333-4333-8333-333333333333',
        success: true,
        response: {},
        transcripts: []
      };
      await callbackRepository.completeDeepgramCallback(data);
      expect(fakeRequest.execute.secondCall.args[0]).to.equal('dbo.transcription_CompleteCallback');
      const bindings = Object.fromEntries(
        fakeRequest.input.args.map(([name, , value]) => [name, value])
      );
      expect(bindings).to.include({
        CorrelationId: correlationId,
        RecordingId: 7,
        Success: true,
        ResponseJSON: '{}',
        TranscriptsJSON: '[]'
      });
      expect(bindings.TokenHash.length).to.equal(32);
      fakeRequest.execute.resolves({ recordset: [] });
      let failure;
      try {
        await callbackRepository.completeDeepgramCallback(data);
      } catch (error) {
        failure = error;
      }
      expect(failure?.message).to.include('did not return a result');
    } finally {
      if (original === undefined) delete pools.AUTOCALLDB;
      else pools.AUTOCALLDB = original;
    }
  });
});

describe('Deepgram callback normalization', () => {
  const requestId = '33333333-3333-4333-8333-333333333333';
  const context = {
    Channels: 1,
    ConfigurationJSON: '{"model":"nova-3","utterances":true,"language":"en"}'
  };
  const makeBody = () => ({
    metadata: { request_id: requestId, channels: 1, duration: 2 },
    results: {
      channels: [{ alternatives: [{ transcript: 'Hello' }] }],
      utterances: [
        {
          start: 0.125,
          end: 1.525,
          channel: 0,
          transcript: 'Hello',
          confidence: 0.95,
          words: [{ word: 'Hello', start: 0.125, end: 1.525, confidence: 0.95 }]
        }
      ]
    }
  });
  it('converts utterances to explicit millisecond rows', () => {
    const result = normalizeDeepgramCallback(makeBody(), context);
    expect(result.success).to.equal(true);
    expect(result.transcripts[0]).to.include({
      sentence: 0,
      channel: 0,
      startTimeMilliseconds: 125,
      endTimeMilliseconds: 1525,
      providerModel: 'nova-3',
      language: 'en'
    });
  });
  it('hashes equivalent JSON consistently regardless of object key order', () => {
    const body = makeBody();
    expect(
      normalizeDeepgramCallback(body, context).payloadHash.equals(
        normalizeDeepgramCallback({ results: body.results, metadata: body.metadata }, context)
          .payloadHash
      )
    ).to.equal(true);
  });
  it('accepts silence with zero transcript rows and sanitized provider failures', () => {
    const body = makeBody();
    body.results.channels[0].alternatives[0].transcript = '';
    body.results.utterances = [];
    expect(normalizeDeepgramCallback(body, context).transcripts).to.deep.equal([]);
    const error = normalizeDeepgramCallback(
      { request_id: requestId, err_code: 'REMOTE_CONTENT_ERROR', err_msg: 'sensitive URL' },
      context
    );
    expect(error.success).to.equal(false);
    expect(error.errorMessage).not.to.include('sensitive');
  });
  for (const change of [
    (body) => {
      body.metadata.channels = 2;
    },
    (body) => {
      body.results.utterances = [];
    },
    (body) => {
      body.results.utterances[0].start = -1;
    },
    (body) => {
      body.results.utterances[0].channel = 1;
    },
    (body) => {
      body.results.utterances[0].confidence = 2;
    },
    (body) => {
      body.results.utterances[0].words[0].end = 9;
    }
  ]) {
    it('rejects malformed or mismatched transcript metadata', () => {
      const body = makeBody();
      change(body);
      expect(normalizeDeepgramCallback(body, context)).to.equal(null);
    });
  }
});
