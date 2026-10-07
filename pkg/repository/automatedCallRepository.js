import sql from 'mssql';
import { executeProcedure } from './sqlserver.js';

export async function startAutomatedCall(
  { callId, providerCallId, phoneE164, startedCallStatus, pendingCallStatus = null },
  { platform = 'AutoCallDB', timeoutMs, transaction } = {}
) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647) {
    throw new TypeError('callId must be a positive SQL integer');
  }
  if (typeof providerCallId !== 'string' || !/^CA[0-9a-fA-F]{32}$/.test(providerCallId)) {
    throw new TypeError('providerCallId must be a Twilio CallSid');
  }
  if (typeof phoneE164 !== 'string' || !/^\+[1-9]\d{1,14}$/.test(phoneE164)) {
    throw new TypeError('phoneE164 must be an E.164 phone number');
  }
  for (const status of [startedCallStatus, pendingCallStatus]) {
    if (status !== null && (!Number.isInteger(status) || status <= 0 || status > 2147483647)) {
      throw new TypeError('call statuses must be positive SQL integers');
    }
  }
  if (startedCallStatus == null || typeof platform !== 'string' || !platform.trim()) {
    throw new TypeError('startedCallStatus and platform are required');
  }

  const result = await executeProcedure('dbo.automatedcalls_Start', {
    params: {
      CallId: { type: sql.Int, val: callId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      PhoneE164: { type: sql.VarChar(16), val: phoneE164 },
      StartedCallStatus: { type: sql.Int, val: startedCallStatus },
      PendingCallStatus: { type: sql.Int, val: pendingCallStatus }
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true
  });

  const context = result.recordset?.[0];
  if (!context) throw new Error('automatedcalls_Start did not return a result');
  return context;
}