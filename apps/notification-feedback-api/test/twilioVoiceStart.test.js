import { describe, it, beforeEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import express from 'express';
import request from 'supertest';
import nconf from 'nconf';
import twilio from 'twilio';
import requireBearerToken from '../app/middleware/requireBearerToken.js';

const savedDependencies = nconf.get('dependencies');
nconf.set('dependencies', { databases: {} });
let createTwilioVoiceRouter;
try {
  ({ createTwilioVoiceRouter } = await import('../app/routes/twilioVoice.js'));
} finally {
  if (savedDependencies === undefined) nconf.clear('dependencies');
  else nconf.set('dependencies', savedDependencies);
}

describe('POST /webhooks/twilio/voice/start', () => {
  const path = '/webhooks/twilio/voice/start?callId=42';
  const body = {
    AccountSid: `AC${'a'.repeat(32)}`,
    CallSid: `CA${'b'.repeat(32)}`,
    To: '+15551234567'
  };
  const xml = '<Response><Say>Hello Alex.</Say></Response>';
  let app;
  let config;
  let startAutomatedCall;

  beforeEach(() => {
    config = {
      accountSid: body.AccountSid,
      authToken: 'test-auth-token',
      startUrl: 'https://feedback.example.com/webhooks/twilio/voice/start',
      database: 'AutoCallDB',
      startedCallStatus: 7,
      pendingCallStatus: null
    };
    startAutomatedCall = sinon.stub().resolves({ Outcome: 'started', TwiML: xml });
    app = express();
    app.use(
      '/webhooks/twilio/voice',
      createTwilioVoiceRouter({
        repository: { startAutomatedCall },
        getConfig: () => config
      })
    );
    app.use(requireBearerToken());
    app.get('/private', (_req, res) => res.sendStatus(200));
  });

  function signedRequest(target = path, payload = body) {
    const query = target.includes('?') ? target.slice(target.indexOf('?')) : '';
    const signature = twilio.getExpectedTwilioSignature(
      'test-auth-token',
      config.startUrl + query,
      payload
    );
    return request(app)
      .post(target)
      .type('form')
      .set('X-Twilio-Signature', signature)
      .send(payload);
  }

  it('returns stored XML without bearer auth and uses only the central database', async () => {
    const response = await signedRequest();
    expect(response.status).to.equal(200);
    expect(response.type).to.equal('application/xml');
    expect(response.text).to.equal(xml);
    expect(response.headers['cache-control']).to.equal('no-store');
    expect(startAutomatedCall.firstCall.args).to.deep.equal([
      {
        callId: 42,
        providerCallId: body.CallSid,
        phoneE164: body.To,
        startedCallStatus: 7,
        pendingCallStatus: null
      },
      { platform: 'AutoCallDB', timeoutMs: 5000 }
    ]);
    expect((await request(app).get('/private')).status).to.equal(401);
  });

  it('serves the same instructions for duplicate callbacks', async () => {
    expect((await signedRequest()).text).to.equal(xml);
    expect((await signedRequest()).text).to.equal(xml);
    expect(startAutomatedCall.calledTwice).to.be.true;
  });

  it('rejects missing signatures before database access', async () => {
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(403);
    expect(startAutomatedCall.notCalled).to.be.true;
  });

  it('rejects a tampered query string', async () => {
    const signature = twilio.getExpectedTwilioSignature(
      config.authToken,
      config.startUrl + '?callId=42',
      body
    );
    const response = await request(app)
      .post(path.replace('42', '43'))
      .type('form')
      .set('X-Twilio-Signature', signature)
      .send(body);
    expect(response.status).to.equal(403);
    expect(startAutomatedCall.notCalled).to.be.true;
  });

  it('rejects a different Twilio account even with a valid signature', async () => {
    expect(
      (await signedRequest(path, { ...body, AccountSid: `AC${'c'.repeat(32)}` })).status
    ).to.equal(403);
    expect(startAutomatedCall.notCalled).to.be.true;
  });

  for (const callId of ['0', '-1', 'abc', '2147483648', '42&callId=43']) {
    it(`rejects invalid callId ${callId}`, async () => {
      expect(
        (await signedRequest(`/webhooks/twilio/voice/start?callId=${callId}`)).status
      ).to.equal(400);
      expect(startAutomatedCall.notCalled).to.be.true;
    });
  }

  for (const [field, value] of [
    ['CallSid', 'invalid'],
    ['To', '5551234567']
  ]) {
    it(`rejects invalid ${field}`, async () => {
      expect((await signedRequest(path, { ...body, [field]: value })).status).to.equal(400);
      expect(startAutomatedCall.notCalled).to.be.true;
    });
  }

  it('rejects JSON requests', async () => {
    expect((await request(app).post(path).send(body)).status).to.equal(415);
    expect(startAutomatedCall.notCalled).to.be.true;
  });

  it('fails closed for missing configuration', async () => {
    config.startedCallStatus = NaN;
    expect((await signedRequest()).status).to.equal(503);
    expect(startAutomatedCall.notCalled).to.be.true;
  });

  for (const [outcome, status] of [
    ['not-found', 404],
    ['provider-mismatch', 409],
    ['destination-mismatch', 409],
    ['call-sid-mismatch', 409],
    ['invalid-twiml', 500]
  ]) {
    it(`handles ${outcome}`, async () => {
      startAutomatedCall.resolves({ Outcome: outcome });
      expect((await signedRequest()).status).to.equal(status);
    });
  }

  it('does not expose database error details', async () => {
    startAutomatedCall.rejects(new Error('Secret database connection details'));
    const response = await signedRequest();
    expect(response.status).to.equal(500);
    expect(response.text).not.to.include('Secret');
  });
});
