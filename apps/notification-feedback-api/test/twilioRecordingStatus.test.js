import { describe, it, beforeEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import express from 'express';
import request from 'supertest';
import nconf from 'nconf';
import twilio from 'twilio';
import sql from 'mssql';
import requireBearerToken from '../app/middleware/requireBearerToken.js';

const savedDependencies = nconf.get('dependencies');
nconf.set('dependencies', { databases: {} });
let createTwilioRecordingsRouter;
try {
  ({ createTwilioRecordingsRouter } = await import('../app/routes/twilioRecordings.js'));
} finally {
  if (savedDependencies === undefined) nconf.clear('dependencies');
  else nconf.set('dependencies', savedDependencies);
}

describe('POST /webhooks/twilio/recordings/status', () => {
  const path = '/webhooks/twilio/recordings/status?callId=42';
  const body = {
    AccountSid: `AC${'a'.repeat(32)}`,
    CallSid: `CA${'b'.repeat(32)}`,
    RecordingSid: `RE${'c'.repeat(32)}`,
    RecordingStatus: 'completed',
    RecordingDuration: '25',
    RecordingChannels: '2',
    RecordingStartTime: 'Thu, 08 Oct 2026 10:00:00 +0000',
    RecordingSource: 'OutboundAPI',
    RecordingTrack: 'both',
    RecordingUrl: `https://api.twilio.com/2010-04-01/Accounts/AC${'a'.repeat(32)}/Recordings/RE${'c'.repeat(32)}`,
    Extra: 'preserve this field'
  };
  let app, config, recordAutomatedCallRecordingStatus;
  beforeEach(() => {
    config = {
      accountSid: body.AccountSid,
      authToken: 'test-auth-token',
      recordingStatusUrl: 'https://feedback.example.com/webhooks/twilio/recordings/status',
      database: 'AutoCallDB'
    };
    recordAutomatedCallRecordingStatus = sinon
      .stub()
      .resolves({ Outcome: 'recorded', RecordingId: 7, Applied: true });
    app = express();
    app.use(
      '/webhooks/twilio/recordings',
      createTwilioRecordingsRouter({
        repository: { recordAutomatedCallRecordingStatus },
        getConfig: () => config
      })
    );
    app.use(requireBearerToken());
    app.get('/private', (_req, res) => res.sendStatus(200));
    app.use((err, _req, res, next) => {
      if (res.headersSent) return next(err);
      return res.sendStatus(err.status ?? 500);
    });
  });
  function signedRequest(target = path, payload = body) {
    const query = target.includes('?') ? target.slice(target.indexOf('?')) : '';
    const signature = twilio.getExpectedTwilioSignature(
      'test-auth-token',
      config.recordingStatusUrl + query,
      payload
    );
    return request(app)
      .post(target)
      .type('form')
      .set('X-Twilio-Signature', signature)
      .send(payload);
  }
  it('persists metadata and all form fields without To, sequence, timestamp, or bearer auth', async () => {
    const response = await signedRequest();
    expect(response.status).to.equal(204);
    expect(response.text).to.equal('');
    expect(response.headers['cache-control']).to.equal('no-store');
    expect(recordAutomatedCallRecordingStatus.firstCall.args).to.deep.equal([
      {
        callId: 42,
        accountSid: body.AccountSid,
        providerCallId: body.CallSid,
        recordingSid: body.RecordingSid,
        recordingStatus: 'completed',
        recordingUrl: body.RecordingUrl,
        durationSeconds: 25,
        channels: 2,
        recordingStartTime: new Date('2026-10-08T10:00:00Z'),
        recordingSource: 'OutboundAPI',
        recordingTrack: 'both',
        parameters: body
      },
      { platform: 'AutoCallDB', timeoutMs: 5000 }
    ]);
    expect((await request(app).get('/private')).status).to.equal(401);
  });
  for (const status of ['in-progress', 'absent', 'failed']) {
    it(`accepts ${status} without media fields`, async () => {
      expect(
        (
          await signedRequest(path, {
            AccountSid: body.AccountSid,
            CallSid: body.CallSid,
            RecordingSid: body.RecordingSid,
            RecordingStatus: status
          })
        ).status
      ).to.equal(204);
    });
  }
  it('acknowledges duplicate, late, and conflicting terminal events', async () => {
    for (const result of [
      { Outcome: 'duplicate' },
      { Outcome: 'recorded', Applied: false, IgnoreReason: 'already-terminal' },
      { Outcome: 'recorded', Applied: false, IgnoreReason: 'terminal-conflict' }
    ]) {
      recordAutomatedCallRecordingStatus.resolves(result);
      expect((await signedRequest()).status).to.equal(204);
    }
  });
  it('rejects missing signatures and wrong accounts before SQL', async () => {
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(403);
    expect(
      (await signedRequest(path, { ...body, AccountSid: `AC${'d'.repeat(32)}` })).status
    ).to.equal(403);
    expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
  });
  it('rejects query and payload tampering', async () => {
    const signature = twilio.getExpectedTwilioSignature(
      config.authToken,
      config.recordingStatusUrl + '?callId=42',
      body
    );
    for (const [target, payload] of [
      [path.replace('42', '43'), body],
      [path, { ...body, RecordingStatus: 'failed' }]
    ]) {
      expect(
        (
          await request(app)
            .post(target)
            .type('form')
            .set('X-Twilio-Signature', signature)
            .send(payload)
        ).status
      ).to.equal(403);
    }
    expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
  });
  it('rejects non-scalar form fields', async () => {
    expect(
      (await signedRequest(path, { ...body, RecordingStatus: ['completed', 'failed'] })).status
    ).to.equal(403);
    expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
  });
  for (const query of ['', '?callId=0', '?callId=2147483648', '?callId=42&callId=43']) {
    it(`rejects invalid call ID ${query}`, async () => {
      expect((await signedRequest('/webhooks/twilio/recordings/status' + query)).status).to.equal(
        400
      );
      expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
    });
  }
  for (const [field, value] of [
    ['CallSid', 'bad'],
    ['RecordingSid', 'bad'],
    ['RecordingStatus', 'processing'],
    ['RecordingDuration', '-1'],
    ['RecordingChannels', '3'],
    ['RecordingStartTime', '0'],
    ['RecordingUrl', 'https://example.com/audio']
  ]) {
    it(`rejects invalid ${field}`, async () => {
      expect((await signedRequest(path, { ...body, [field]: value })).status).to.equal(400);
      expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
    });
  }
  for (const url of [
    undefined,
    'http://feedback.example.com/webhooks/twilio/recordings/status',
    'https://feedback.example.com/webhooks/twilio/voice/status',
    'https://feedback.example.com/webhooks/twilio/recordings/status?x=1',
    'https://user:secret@feedback.example.com/webhooks/twilio/recordings/status'
  ]) {
    it(`fails closed for invalid configured URL ${url}`, async () => {
      config.recordingStatusUrl = url;
      expect((await request(app).post(path).type('form').send(body)).status).to.equal(503);
      expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
    });
  }
  it('rejects JSON and oversized forms', async () => {
    expect((await request(app).post(path).send(body)).status).to.equal(415);
    expect(
      (
        await request(app)
          .post(path)
          .type('form')
          .send({ ...body, Extra: 'x'.repeat(33000) })
      ).status
    ).to.equal(413);
    expect(recordAutomatedCallRecordingStatus.notCalled).to.be.true;
  });
  for (const [outcome, status] of [
    ['not-found', 404],
    ['provider-mismatch', 409],
    ['call-sid-mismatch', 409],
    ['recording-conflict', 409],
    ['invalid-recording', 500]
  ]) {
    it(`maps ${outcome}`, async () => {
      recordAutomatedCallRecordingStatus.resolves({ Outcome: outcome });
      expect((await signedRequest()).status).to.equal(status);
    });
  }
  it('masks SQL errors', async () => {
    recordAutomatedCallRecordingStatus.rejects(new Error('Secret connection details'));
    const response = await signedRequest();
    expect(response.status).to.equal(500);
    expect(response.text).not.to.include('Secret');
  });
  it('binds the real repository SQL parameters using a fake pool', async () => {
    const { initialize } = await import('../../../pkg/repository/sqlserver.js');
    const { recordAutomatedCallRecordingStatus: save } =
      await import('../../../pkg/repository/automatedCallRepository.js');
    const pools = await initialize();
    const original = pools.AUTOCALLDB;
    const fakeRequest = {
      input: sinon.stub(),
      execute: sinon.stub().resolves({ recordset: [{ Outcome: 'recorded', RecordingId: 7 }] })
    };
    try {
      pools.AUTOCALLDB = Promise.resolve({ request: () => fakeRequest });
      const result = await save(
        {
          callId: 42,
          providerCallId: body.CallSid,
          accountSid: body.AccountSid,
          recordingSid: body.RecordingSid,
          recordingStatus: 'completed',
          recordingUrl: body.RecordingUrl,
          durationSeconds: 25,
          channels: 2,
          recordingStartTime: new Date('2026-10-08T10:00:00Z'),
          parameters: body
        },
        { timeoutMs: 5000 }
      );
      expect(result.RecordingId).to.equal(7);
      expect(fakeRequest.execute.calledOnceWithExactly('dbo.automatedcalls_RecordingStatus')).to.be
        .true;
      expect(fakeRequest.timeout).to.equal(5000);
      const bindings = Object.fromEntries(
        fakeRequest.input.args.map(([name, type, value]) => [name, { type, value }])
      );
      expect(bindings.CallId).to.deep.equal({ type: sql.Int, value: 42 });
      expect(bindings.Channels).to.deep.equal({ type: sql.TinyInt, value: 2 });
      expect(bindings.RecordingStartTime.type.scale).to.equal(3);
      expect(bindings.RecordingSid.value).to.equal(body.RecordingSid);
      expect(JSON.parse(bindings.ParametersJSON.value)).to.deep.equal(body);
      fakeRequest.execute.resolves({ recordset: [] });
      let error;
      try {
        await save({
          callId: 42,
          providerCallId: body.CallSid,
          accountSid: body.AccountSid,
          recordingSid: body.RecordingSid,
          recordingStatus: 'absent'
        });
      } catch (caught) {
        error = caught;
      }
      expect(error?.message).to.equal('automatedcalls_RecordingStatus did not return a result');
    } finally {
      if (original === undefined) delete pools.AUTOCALLDB;
      else pools.AUTOCALLDB = original;
    }
  });
});
