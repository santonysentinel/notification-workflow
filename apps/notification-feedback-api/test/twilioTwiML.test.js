import { describe, it } from 'mocha';
import { expect } from 'chai';
import { isVoiceResponse, prepareOpeningStep } from '../app/utils/twilioTwiML.js';

describe('Twilio TwiML preparation', () => {
  const bundle =
    '<CallFlow initialStep="opening"><Step id="opening"><Response><Say>Hello Alex &amp; Sam.</Say><Redirect>https://example.com/next?callId=42&amp;executionId={{executionId}}</Redirect></Response></Step><Step id="later"><Response><Say>Later step.</Say></Response></Step></CallFlow>';

  it('selects only the opening response and substitutes a fresh execution ID', () => {
    const candidate = prepareOpeningStep(bundle);
    expect(candidate.stepId).to.equal('opening');
    expect(candidate.executionId).to.match(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
    );
    expect(candidate.expectedTwiML).to.equal(bundle);
    expect(candidate.responseTwiML).to.include('Hello Alex &amp; Sam.');
    expect(candidate.responseTwiML).to.include(`&amp;executionId=${candidate.executionId}`);
    expect(candidate.responseTwiML).not.to.include('CallFlow');
    expect(candidate.responseTwiML).not.to.include('Later step.');
    expect(isVoiceResponse(candidate.responseTwiML)).to.be.true;
    expect(prepareOpeningStep(bundle).executionId).not.to.equal(candidate.executionId);
  });

  it('preserves CDATA, nested verbs, attributes, and repeated execution placeholders', () => {
    const candidate = prepareOpeningStep(
      '<CallFlow initialStep="opening"><Step id="opening"><Response><Gather input="dtmf speech" action="https://example.com/next?executionId={{executionId}}"><Say><![CDATA[Alex & Sam <3]]></Say></Gather><Redirect>https://example.com/next?executionId={{executionId}}</Redirect></Response></Step></CallFlow>'
    );
    expect(candidate.responseTwiML).to.include('<![CDATA[Alex & Sam <3]]>');
    expect(candidate.responseTwiML).to.include('input="dtmf speech"');
    expect(candidate.responseTwiML.split(candidate.executionId)).to.have.length(3);
  });

  for (const xml of [
    null,
    '',
    'not XML',
    '<Response/>',
    '<CallFlow><Step id="opening"><Response/></Step></CallFlow>',
    '<CallFlow initialStep=" "><Step id=" "><Response/></Step></CallFlow>',
    `<CallFlow initialStep="${'a'.repeat(201)}"/>`,
    '<CallFlow initialStep="opening"><Step id="Opening"><Response/></Step></CallFlow>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response/></Step><Step id="opening"><Response/></Step></CallFlow>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response/><Response/></Step></CallFlow>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response><Say>{{name}}</Say></Response></Step></CallFlow>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response/></Step></CallFlow><Other/>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response></Step></CallFlow>',
    '<CallFlow initialStep=opening><Step id="opening"><Response/></Step></CallFlow>',
    '<CallFlow initialStep="opening"><Step id="opening"><Response><Say>&unknown;</Say></Response></Step></CallFlow>',
    '<CallFlow xmlns="urn:unexpected" initialStep="opening"><Step id="opening"><Response/></Step></CallFlow>',
    '<!DOCTYPE CallFlow [<!ENTITY name SYSTEM "file:///secret">]><CallFlow initialStep="opening"><Step id="opening"><Response><Say>&name;</Say></Response></Step></CallFlow>'
  ]) {
    it(`rejects invalid bundle ${String(xml).slice(0, 70)}`, () => {
      expect(prepareOpeningStep(xml)).to.equal(null);
    });
  }

  it('validates issued and replayed responses without accepting bundles or malformed XML', () => {
    expect(isVoiceResponse('<Response><Say>Hello.</Say></Response>')).to.be.true;
    for (const xml of [
      null,
      bundle,
      '<Response/><Other/>',
      '<Response>',
      '<Response>{{name}}</Response>'
    ]) {
      expect(isVoiceResponse(xml)).to.be.false;
    }
  });
});
