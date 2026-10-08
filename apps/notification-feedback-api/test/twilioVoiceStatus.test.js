import { describe, it, beforeEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import express from 'express';
import request from 'supertest';
import nconf from 'nconf';
import twilio from 'twilio';
import requireBearerToken from '../app/middleware/requireBearerToken.js';
import sql from 'mssql';

const savedDependencies = nconf.get('dependencies');
nconf.set('dependencies', { databases: {} });
let createTwilioVoiceRouter;
try {
  ({ createTwilioVoiceRouter } = await import('../app/routes/twilioVoice.js'));
} finally {
  if (savedDependencies === undefined) nconf.clear('dependencies');
  else nconf.set('dependencies', savedDependencies);
}

describe('POST /webhooks/twilio/voice/status', () => {
  const path = '/webhooks/twilio/voice/status?callId=42';
  const body = {
    AccountSid: `AC${'a'.repeat(32)}`,
    CallSid: `CA${'b'.repeat(32)}`,
    To: '+15551234567',
    CallStatus: 'completed',
    SequenceNumber: '3',
    Timestamp: 'Thu, 08 Oct 2026 10:00:00 +0000',
    CallDuration: '25'
  };
  let app, config, recordAutomatedCallStatus;
  beforeEach(() => {
    config = {
      accountSid: body.AccountSid,
      authToken: 'test-auth-token',
      statusUrl: 'https://feedback.example.com/webhooks/twilio/voice/status',
      database: 'AutoCallDB'
    };
    recordAutomatedCallStatus = sinon
      .stub()
      .resolves({ Outcome: 'recorded', Applied: true, QueueFinalized: true });
    app = express();
    app.use(
      '/webhooks/twilio/voice',
      createTwilioVoiceRouter({
        repository: { recordAutomatedCallStatus },
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
      config.statusUrl + query,
      payload
    );
    return request(app)
      .post(target)
      .type('form')
      .set('X-Twilio-Signature', signature)
      .send(payload);
  }
  it('records typed status fields and returns empty 204 without bearer auth', async () => {
    const response = await signedRequest();
    expect(response.status).to.equal(204);
    expect(response.text).to.equal('');
    expect(response.headers['cache-control']).to.equal('no-store');
    expect(recordAutomatedCallStatus.firstCall.args).to.deep.equal([
      {
        callId: 42,
        providerCallId: body.CallSid,
        phoneE164: body.To,
        callStatus: 'completed',
        sequenceNumber: 3,
        timestamp: new Date('2026-10-08T10:00:00Z'),
        callDurationSeconds: 25,
        sipResponseCode: null,
        parameters: body
      },
      { platform: 'AutoCallDB', timeoutMs: 5000 }
    ]);
    expect((await request(app).get('/private')).status).to.equal(401);
  });
  for (const callStatus of [
    'queued',
    'initiated',
    'ringing',
    'in-progress',
    'completed',
    'busy',
    'failed',
    'no-answer',
    'canceled'
  ]) {
    it(`accepts ${callStatus}`, async () => {
      expect((await signedRequest(path, { ...body, CallStatus: callStatus })).status).to.equal(204);
    });
  }
  it('acknowledges duplicate and ignored older callbacks', async () => {
    recordAutomatedCallStatus.resolves({ Outcome: 'duplicate', Applied: false });
    expect((await signedRequest()).status).to.equal(204);
    recordAutomatedCallStatus.resolves({
      Outcome: 'recorded',
      Applied: false,
      IgnoreReason: 'older-sequence'
    });
    expect((await signedRequest()).status).to.equal(204);
  });
  it('rejects missing signatures and unexpected accounts before SQL access', async () => {
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(403);
    expect(
      (await signedRequest(path, { ...body, AccountSid: `AC${'c'.repeat(32)}` })).status
    ).to.equal(403);
    expect(recordAutomatedCallStatus.notCalled).to.be.true;
  });
  it('rejects query and form tampering', async () => {
    const signature = twilio.getExpectedTwilioSignature(
      config.authToken,
      config.statusUrl + '?callId=42',
      body
    );
    expect(
      (
        await request(app)
          .post(path.replace('42', '43'))
          .type('form')
          .set('X-Twilio-Signature', signature)
          .send(body)
      ).status
    ).to.equal(403);
    expect(
      (
        await request(app)
          .post(path)
          .type('form')
          .set('X-Twilio-Signature', signature)
          .send({ ...body, CallStatus: 'failed' })
      ).status
    ).to.equal(403);
    expect(recordAutomatedCallStatus.notCalled).to.be.true;
  });
  for (const query of ['callId=0', 'callId=2147483648', 'callId=42&callId=43', '']) {
    it(`rejects invalid query ${query}`, async () => {
      expect(
        (await signedRequest('/webhooks/twilio/voice/status' + (query ? '?' + query : ''))).status
      ).to.equal(400);
      expect(recordAutomatedCallStatus.notCalled).to.be.true;
    });
  }
  for (const [field, value] of [
    ['CallSid', 'bad'],
    ['To', '5551234567'],
    ['CallStatus', 'answered'],
    ['SequenceNumber', undefined],
    ['SequenceNumber', '-1'],
    ['Timestamp', 'invalid'],
    ['CallDuration', '-1'],
    ['SipResponseCode', '700']
  ]) {
    it(`rejects invalid ${field}`, async () => {
      const payload = { ...body, [field]: value };
      if (value === undefined) delete payload[field];
      expect((await signedRequest(path, payload)).status).to.equal(400);
      expect(recordAutomatedCallStatus.notCalled).to.be.true;
    });
  }
  it('rejects JSON, oversized forms, and invalid endpoint configuration', async () => {
    expect((await request(app).post(path).send(body)).status).to.equal(415);
    expect(
      (
        await request(app)
          .post(path)
          .type('form')
          .send({ ...body, Extra: 'a'.repeat(33000) })
      ).status
    ).to.equal(413);
    config.statusUrl = undefined;
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(503);
    expect(recordAutomatedCallStatus.notCalled).to.be.true;
  });
  for (const [outcome, status] of [
    ['not-found', 404],
    ['provider-mismatch', 409],
    ['destination-mismatch', 409],
    ['call-sid-mismatch', 409],
    ['invalid-status', 500]
  ]) {
    it(`handles ${outcome}`, async () => {
      recordAutomatedCallStatus.resolves({ Outcome: outcome });
      expect((await signedRequest()).status).to.equal(status);
    });
  }
  it('does not expose database errors', async () => {
    recordAutomatedCallStatus.rejects(new Error('Secret connection details'));
    const response = await signedRequest();
    expect(response.status).to.equal(500);
    expect(response.text).not.to.include('Secret');
  });

  it('binds typed status parameters through the real repository without a SQL connection', async () => {
    const { initialize } = await import('../../../pkg/repository/sqlserver.js');
    const repository = await import('../../../pkg/repository/automatedCallRepository.js');
    const pools = await initialize();
    const fakeRequest = {
      input: sinon.stub(),
      execute: sinon.stub().resolves({ recordset: [{ Outcome: 'recorded', Applied: true }] })
    };
    const poolKeys = ['AUTOCALLDB'];
    const originals = new Map(poolKeys.map((key) => [key, pools[key]]));
    try {
      for (const key of poolKeys) pools[key] = Promise.resolve({ request: () => fakeRequest });
      const timestamp = new Date('2026-10-08T10:00:00Z');
      const result = await repository.recordAutomatedCallStatus(
        {
          callId: 42,
          providerCallId: body.CallSid,
          phoneE164: body.To,
          callStatus: 'completed',
          sequenceNumber: 3,
          timestamp,
          callDurationSeconds: 25,
          sipResponseCode: 200,
          parameters: body
        },
        { platform: 'AutoCallDB', timeoutMs: 5000 }
      );
      expect(result).to.deep.equal({ Outcome: 'recorded', Applied: true });
      expect(fakeRequest.execute.calledOnceWithExactly('dbo.automatedcalls_Status')).to.be.true;
      expect(fakeRequest.timeout).to.equal(5000);
      const bindings = Object.fromEntries(
        fakeRequest.input.args.map(([name, type, value]) => [name, { type, value }])
      );
      expect(bindings.CallId).to.deep.equal({ type: sql.Int, value: 42 });
      expect(bindings.SequenceNumber).to.deep.equal({ type: sql.Int, value: 3 });
      expect(bindings.CallStatus.value).to.equal('completed');
      expect(bindings.Timestamp.value).to.equal(timestamp);
      expect(bindings.Timestamp.type.scale).to.equal(3);
      expect(bindings.CallDurationSeconds.value).to.equal(25);
      expect(bindings.SipResponseCode.value).to.equal(200);
      expect(JSON.parse(bindings.ParametersJSON.value)).to.deep.equal(body);
      fakeRequest.execute.resolves({ recordset: [] });
      let error;
      try {
        await repository.recordAutomatedCallStatus({
          callId: 42,
          providerCallId: body.CallSid,
          phoneE164: body.To,
          callStatus: 'completed',
          sequenceNumber: 3,
          timestamp
        });
      } catch (caught) {
        error = caught;
      }
      expect(error?.message).to.equal('automatedcalls_Status did not return a result');
    } finally {
      for (const [key, value] of originals) {
        if (value === undefined) delete pools[key];
        else pools[key] = value;
      }
    }
  });
});
