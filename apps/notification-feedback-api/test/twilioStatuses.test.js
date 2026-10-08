import { describe, it } from 'mocha';
import { expect } from 'chai';
import { callStatuses, parseStatusCallback } from '../app/utils/twilioStatuses.js';

describe('Twilio status callback parsing', () => {
  const body = {
    CallStatus: 'completed',
    SequenceNumber: '3',
    Timestamp: 'Thu, 08 Oct 2026 10:00:00 +0000',
    CallDuration: '25',
    SipResponseCode: '200'
  };

  for (const callStatus of callStatuses) {
    it(`accepts ${callStatus} and preserves typed reporting values`, () => {
      const result = parseStatusCallback({ ...body, CallStatus: callStatus });
      expect(result).to.include({
        callStatus,
        sequenceNumber: 3,
        callDurationSeconds: 25,
        sipResponseCode: 200
      });
      expect(result.timestamp.toISOString()).to.equal('2026-10-08T10:00:00.000Z');
    });
  }

  it('accepts sequence zero and missing optional reporting values', () => {
    expect(
      parseStatusCallback({
        CallStatus: 'initiated',
        SequenceNumber: '0',
        Timestamp: body.Timestamp
      })
    ).to.include({ sequenceNumber: 0, callDurationSeconds: null, sipResponseCode: null });
  });

  for (const [field, value] of [
    ['CallStatus', 'answered'],
    ['CallStatus', 'COMPLETED'],
    ['SequenceNumber', undefined],
    ['SequenceNumber', '-1'],
    ['SequenceNumber', '1.5'],
    ['SequenceNumber', '2147483648'],
    ['Timestamp', undefined],
    ['Timestamp', 'not a date'],
    ['Timestamp', ''],
    ['Timestamp', '0'],
    ['Timestamp', '2026-10-08T10:00:00Z'],
    ['Timestamp', 'Thu, 08 Oct 2026 10:00:00 +0100'],
    ['Timestamp', 'Wed, 08 Oct 2026 10:00:00 +0000'],
    ['Timestamp', 'Mon, 30 Feb 2026 10:00:00 +0000'],
    ['CallDuration', '-1'],
    ['CallDuration', ''],
    ['CallDuration', '2147483648'],
    ['SipResponseCode', '99'],
    ['SipResponseCode', '700']
  ]) {
    it(`rejects invalid ${field} ${value}`, () => {
      expect(parseStatusCallback({ ...body, [field]: value })).to.equal(null);
    });
  }
});
