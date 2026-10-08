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

describe('POST /webhooks/twilio/voice/next', () => {
  const executionId = '11111111-1111-4111-8111-111111111111';
  const path = `/webhooks/twilio/voice/next?callId=42&executionId=${executionId}`;
  const body = {
    AccountSid: `AC${'a'.repeat(32)}`,
    CallSid: `CA${'b'.repeat(32)}`,
    To: '+15551234567'
  };
  const xml = '<Response><Say>Thanks.</Say><Hangup/></Response>';
  const templateJSON = JSON.stringify({
    schemaVersion: 1,
    initialStep: 'question',
    steps: [
      {
        id: 'question',
        type: 'gather',
        transitions: {
          digits: { 1: 'thanks' },
          speech: { yes: 'thanks' },
          noInput: 'thanks',
          fallback: 'thanks'
        }
      },
      { id: 'thanks', type: 'terminal' }
    ]
  });
  const bundle = `<CallFlow initialStep="question"><Step id="question"><Response><Gather/></Response></Step><Step id="thanks">${xml}</Step></CallFlow>`;
  let app;
  let config;
  let getAutomatedCallNextContext;
  let advanceAutomatedCall;

  beforeEach(() => {
    config = {
      accountSid: body.AccountSid,
      authToken: 'test-auth-token',
      nextUrl: 'https://feedback.example.com/webhooks/twilio/voice/next',
      database: 'AutoCallDB'
    };
    getAutomatedCallNextContext = sinon.stub().resolves({
      TwiML: bundle,
      TemplateJSON: templateJSON,
      SourceEventJSON: JSON.stringify({ stepId: 'question', executionId })
    });
    advanceAutomatedCall = sinon
      .stub()
      .resolves({ Outcome: 'advanced', ResponseTwiML: xml, Replayed: false });
    app = express();
    app.use(
      '/webhooks/twilio/voice',
      createTwilioVoiceRouter({
        repository: { getAutomatedCallNextContext, advanceAutomatedCall },
        getConfig: () => config
      })
    );
    app.use(requireBearerToken());
    app.get('/private', (_req, res) => res.sendStatus(200));
  });

  function signedRequest(target = path, payload = body, signedTarget = target) {
    const query = signedTarget.includes('?') ? signedTarget.slice(signedTarget.indexOf('?')) : '';
    const signature = twilio.getExpectedTwilioSignature(
      'test-auth-token',
      config.nextUrl + query,
      payload
    );
    return request(app)
      .post(target)
      .type('form')
      .set('X-Twilio-Signature', signature)
      .send(payload);
  }

  for (const [input, inputType] of [
    [{ Digits: '1' }, 'dtmf'],
    [{ SpeechResult: ' YES ', Confidence: '0.9' }, 'speech'],
    [{}, 'no-input']
  ]) {
    it(`prepares and returns the destination for ${inputType}`, async () => {
      const response = await signedRequest(path, { ...body, ...input });
      expect(response.status).to.equal(200);
      expect(response.type).to.equal('application/xml');
      expect(response.headers['cache-control']).to.equal('no-store');
      expect(response.text).to.equal(xml);
      expect(getAutomatedCallNextContext.firstCall.args).to.deep.equal([
        { callId: 42, executionId },
        { platform: 'AutoCallDB', timeoutMs: 5000 }
      ]);
      const args = advanceAutomatedCall.firstCall.args[0];
      expect(args).to.include({
        callId: 42,
        executionId,
        providerCallId: body.CallSid,
        phoneE164: body.To
      });
      expect(args.nextStep).to.include({
        sourceStepId: 'question',
        stepId: 'thanks',
        responseTwiML: xml,
        inputType,
        expectedTwiML: bundle,
        expectedTemplateJSON: templateJSON
      });
      expect((await request(app).get('/private')).status).to.equal(401);
    });
  }

  it('replays a saved response even if the graph or bundle becomes invalid', async () => {
    getAutomatedCallNextContext.resolves({
      TwiML: 'invalid',
      TemplateJSON: 'invalid',
      SourceEventJSON: '{}'
    });
    advanceAutomatedCall.resolves({ Outcome: 'advanced', ResponseTwiML: xml, Replayed: true });
    expect((await signedRequest()).text).to.equal(xml);
    expect(advanceAutomatedCall.firstCall.args[0].nextStep).to.equal(null);
  });

  it('sends invalid preparation to SQL for authoritative replay/error checks', async () => {
    getAutomatedCallNextContext.resolves(null);
    advanceAutomatedCall.resolves({ Outcome: 'execution-not-found' });
    expect((await signedRequest()).status).to.equal(404);
    expect(advanceAutomatedCall.firstCall.args[0].nextStep).to.equal(null);
  });

  it('rejects unsigned callbacks before database access', async () => {
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(403);
    expect(getAutomatedCallNextContext.notCalled).to.be.true;
  });

  it('rejects tampered execution IDs and form input', async () => {
    expect(
      (
        await signedRequest(
          path.replace(executionId, '22222222-2222-4222-8222-222222222222'),
          body,
          path
        )
      ).status
    ).to.equal(403);
    const signature = twilio.getExpectedTwilioSignature(
      config.authToken,
      config.nextUrl + path.slice(path.indexOf('?')),
      { ...body, Digits: '1' }
    );
    expect(
      (
        await request(app)
          .post(path)
          .type('form')
          .set('X-Twilio-Signature', signature)
          .send({ ...body, Digits: '2' })
      ).status
    ).to.equal(403);
    expect(getAutomatedCallNextContext.notCalled).to.be.true;
  });

  it('rejects a different account even with a valid signature', async () => {
    expect(
      (await signedRequest(path, { ...body, AccountSid: `AC${'c'.repeat(32)}` })).status
    ).to.equal(403);
    expect(getAutomatedCallNextContext.notCalled).to.be.true;
  });

  for (const query of [
    'callId=0',
    'callId=2147483648',
    'callId=42',
    `callId=42&executionId=bad`,
    `callId=42&executionId=${executionId}&executionId=${executionId}`,
    `callId=42&callId=43&executionId=${executionId}`
  ]) {
    it(`rejects invalid query ${query}`, async () => {
      expect((await signedRequest(`/webhooks/twilio/voice/next?${query}`)).status).to.equal(400);
      expect(getAutomatedCallNextContext.notCalled).to.be.true;
    });
  }

  for (const [field, value] of [
    ['CallSid', 'bad'],
    ['To', '5551234567'],
    ['Digits', 'yes'],
    ['Confidence', '1.1'],
    ['Confidence', 'NaN']
  ]) {
    it(`rejects invalid ${field}`, async () => {
      expect((await signedRequest(path, { ...body, [field]: value })).status).to.equal(400);
      expect(getAutomatedCallNextContext.notCalled).to.be.true;
    });
  }

  it('rejects JSON and missing next configuration', async () => {
    expect((await request(app).post(path).send(body)).status).to.equal(415);
    config.nextUrl = undefined;
    expect((await request(app).post(path).type('form').send(body)).status).to.equal(503);
    expect(getAutomatedCallNextContext.notCalled).to.be.true;
  });

  for (const [outcome, status] of [
    ['not-found', 404],
    ['execution-not-found', 404],
    ['provider-mismatch', 409],
    ['destination-mismatch', 409],
    ['call-sid-mismatch', 409],
    ['stale-execution', 409],
    ['context-changed', 409],
    ['invalid-flow', 500]
  ]) {
    it(`handles ${outcome}`, async () => {
      advanceAutomatedCall.resolves({ Outcome: outcome });
      expect((await signedRequest()).status).to.equal(status);
    });
  }

  it('does not return malformed replay XML', async () => {
    advanceAutomatedCall.resolves({ Outcome: 'advanced', ResponseTwiML: bundle, Replayed: true });
    expect((await signedRequest()).status).to.equal(500);
  });

  it('masks database failures', async () => {
    advanceAutomatedCall.rejects(new Error('Secret connection details'));
    const response = await signedRequest();
    expect(response.status).to.equal(500);
    expect(response.text).not.to.include('Secret');
  });
});
