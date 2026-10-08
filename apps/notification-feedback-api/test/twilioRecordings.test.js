import { describe, it } from 'mocha';
import { expect } from 'chai';
import { parseRecordingCallback } from '../app/utils/twilioRecordings.js';

describe('Twilio recording callback parsing', () => {
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
    RecordingUrl: `https://api.twilio.com/2010-04-01/Accounts/AC${'a'.repeat(32)}/Recordings/RE${'c'.repeat(32)}`
  };
  it('parses completed metadata without requiring call-progress fields or To', () => {
    expect(parseRecordingCallback(body)).to.deep.equal({
      accountSid: body.AccountSid,
      providerCallId: body.CallSid,
      recordingSid: body.RecordingSid,
      recordingStatus: 'completed',
      recordingUrl: body.RecordingUrl,
      durationSeconds: 25,
      channels: 2,
      recordingStartTime: new Date('2026-10-08T10:00:00Z'),
      recordingSource: 'OutboundAPI',
      recordingTrack: 'both'
    });
  });
  for (const recordingStatus of ['in-progress', 'absent', 'failed']) {
    it(`accepts ${recordingStatus} without media metadata`, () => {
      expect(
        parseRecordingCallback({
          AccountSid: body.AccountSid,
          CallSid: body.CallSid,
          RecordingSid: body.RecordingSid,
          RecordingStatus: recordingStatus
        })
      ).to.include({
        recordingStatus,
        recordingUrl: null,
        durationSeconds: null,
        channels: null,
        recordingStartTime: null
      });
    });
  }
  it('preserves zero duration and accepts ISO UTC timestamps', () => {
    expect(
      parseRecordingCallback({
        ...body,
        RecordingDuration: '0',
        RecordingStartTime: '2026-10-08T10:00:00Z'
      })
    ).to.include({ durationSeconds: 0 });
    expect(
      parseRecordingCallback({
        ...body,
        RecordingStartTime: '2026-10-08T10:00:00.123Z'
      }).recordingStartTime.toISOString()
    ).to.equal('2026-10-08T10:00:00.123Z');
  });
  it('accepts Twilio regional media hosts and legacy HTTP locations', () => {
    expect(
      parseRecordingCallback({
        ...body,
        RecordingUrl: body.RecordingUrl.replace('https://api.', 'http://api.sydney.au1.')
      })
    ).not.to.equal(null);
  });
  it('accepts WAV and MP3 media locations scoped to the same account and call', () => {
    for (const suffix of ['', '.wav', '.mp3']) {
      expect(
        parseRecordingCallback({
          ...body,
          RecordingUrl:
            body.RecordingUrl.replace('/Recordings/', `/Calls/${body.CallSid}/Recordings/`) + suffix
        })
      ).not.to.equal(null);
    }
  });
  for (const [field, value] of [
    ['AccountSid', 'bad'],
    ['CallSid', 'bad'],
    ['RecordingSid', 'bad'],
    ['RecordingStatus', 'processing'],
    ['RecordingDuration', '-1'],
    ['RecordingDuration', '2147483648'],
    ['RecordingDuration', '1.5'],
    ['RecordingDuration', undefined],
    ['RecordingChannels', '0'],
    ['RecordingChannels', '3'],
    ['RecordingChannels', undefined],
    ['RecordingUrl', undefined],
    ['RecordingUrl', 'https://example.com/audio'],
    ['RecordingUrl', body.RecordingUrl + '?token=secret'],
    ['RecordingUrl', body.RecordingUrl.replace(body.RecordingSid, `RE${'d'.repeat(32)}`)],
    ['RecordingStartTime', '0'],
    ['RecordingStartTime', '0000-01-01T00:00:00Z'],
    ['RecordingStartTime', '2026-02-30T10:00:00Z'],
    ['RecordingStartTime', 'Wed, 08 Oct 2026 10:00:00 +0000'],
    ['RecordingSource', 'x'.repeat(101)],
    ['RecordingTrack', 'all']
  ]) {
    it(`rejects invalid ${field} ${value}`, () => {
      expect(parseRecordingCallback({ ...body, [field]: value })).to.equal(null);
    });
  }
});
