import sql from "mssql";
import { executeProcedure } from "./sqlserver.js";

export async function getAutomatedCallTwiML(
  { callId },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647) {
    throw new TypeError("callId must be a positive SQL integer");
  }
  if (typeof platform !== "string" || !platform.trim()) {
    throw new TypeError("platform is required");
  }
  const result = await executeProcedure("dbo.automatedcalls_GetTwiML", {
    params: { CallId: { type: sql.Int, val: callId } },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });
  return result.recordset?.[0]?.TwiML ?? null;
}

export async function startAutomatedCall(
  { callId, providerCallId, phoneE164, openingStep = null },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647) {
    throw new TypeError("callId must be a positive SQL integer");
  }
  if (
    typeof providerCallId !== "string" ||
    !/^CA[0-9a-fA-F]{32}$/.test(providerCallId)
  ) {
    throw new TypeError("providerCallId must be a Twilio CallSid");
  }
  if (typeof phoneE164 !== "string" || !/^\+[1-9]\d{1,14}$/.test(phoneE164)) {
    throw new TypeError("phoneE164 must be an E.164 phone number");
  }
  if (typeof platform !== "string" || !platform.trim()) {
    throw new TypeError("platform is required");
  }

  const result = await executeProcedure("dbo.automatedcalls_Start", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      PhoneE164: { type: sql.VarChar(16), val: phoneE164 },
      ExpectedTwiML: {
        type: sql.NVarChar(sql.MAX),
        val: openingStep?.expectedTwiML ?? null,
      },
      StepId: { type: sql.NVarChar(200), val: openingStep?.stepId ?? null },
      ExecutionId: {
        type: sql.UniqueIdentifier,
        val: openingStep?.executionId ?? null,
      },
      ResponseTwiML: {
        type: sql.NVarChar(sql.MAX),
        val: openingStep?.responseTwiML ?? null,
      },
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });

  const context = result.recordset?.[0];
  if (!context) throw new Error("automatedcalls_Start did not return a result");
  return context;
}

function validateExecution(callId, executionId, platform) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647) {
    throw new TypeError("callId must be a positive SQL integer");
  }
  if (
    typeof executionId !== "string" ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      executionId,
    )
  ) {
    throw new TypeError("executionId must be a UUID");
  }
  if (typeof platform !== "string" || !platform.trim()) {
    throw new TypeError("platform is required");
  }
}

export async function getAutomatedCallNextContext(
  { callId, executionId },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  validateExecution(callId, executionId, platform);
  const result = await executeProcedure("dbo.automatedcalls_GetNextContext", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ExecutionId: { type: sql.UniqueIdentifier, val: executionId },
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });
  return result.recordset?.[0] ?? null;
}

export async function advanceAutomatedCall(
  { callId, executionId, providerCallId, phoneE164, input, nextStep = null },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  validateExecution(callId, executionId, platform);
  if (
    typeof providerCallId !== "string" ||
    !/^CA[0-9a-fA-F]{32}$/.test(providerCallId)
  ) {
    throw new TypeError("providerCallId must be a Twilio CallSid");
  }
  if (typeof phoneE164 !== "string" || !/^\+[1-9]\d{1,14}$/.test(phoneE164)) {
    throw new TypeError("phoneE164 must be an E.164 phone number");
  }
  const result = await executeProcedure("dbo.automatedcalls_Next", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ExecutionId: { type: sql.UniqueIdentifier, val: executionId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      PhoneE164: { type: sql.VarChar(16), val: phoneE164 },
      InputJSON: { type: sql.NVarChar(sql.MAX), val: JSON.stringify(input) },
      InputType: { type: sql.VarChar(20), val: nextStep?.inputType ?? null },
      ExpectedTwiML: {
        type: sql.NVarChar(sql.MAX),
        val: nextStep?.expectedTwiML ?? null,
      },
      ExpectedTemplateJSON: {
        type: sql.NVarChar(sql.MAX),
        val: nextStep?.expectedTemplateJSON ?? null,
      },
      SourceStepId: {
        type: sql.NVarChar(200),
        val: nextStep?.sourceStepId ?? null,
      },
      StepId: { type: sql.NVarChar(200), val: nextStep?.stepId ?? null },
      NextExecutionId: {
        type: sql.UniqueIdentifier,
        val: nextStep?.executionId ?? null,
      },
      ResponseTwiML: {
        type: sql.NVarChar(sql.MAX),
        val: nextStep?.responseTwiML ?? null,
      },
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });
  const context = result.recordset?.[0];
  if (!context) throw new Error("automatedcalls_Next did not return a result");
  return context;
}

export async function recordAutomatedCallStatus(
  {
    callId,
    providerCallId,
    phoneE164,
    callStatus,
    sequenceNumber,
    timestamp,
    callDurationSeconds = null,
    sipResponseCode = null,
    parameters,
  },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647)
    throw new TypeError("callId must be a positive SQL integer");
  if (
    typeof providerCallId !== "string" ||
    !/^CA[0-9a-fA-F]{32}$/.test(providerCallId)
  )
    throw new TypeError("providerCallId must be a Twilio CallSid");
  if (typeof phoneE164 !== "string" || !/^\+[1-9]\d{1,14}$/.test(phoneE164))
    throw new TypeError("phoneE164 must be an E.164 phone number");
  if (
    ![
      "queued",
      "initiated",
      "ringing",
      "in-progress",
      "completed",
      "busy",
      "failed",
      "no-answer",
      "canceled",
    ].includes(callStatus)
  )
    throw new TypeError("Unsupported Twilio call status");
  if (
    !Number.isInteger(sequenceNumber) ||
    sequenceNumber < 0 ||
    sequenceNumber > 2147483647 ||
    !(timestamp instanceof Date) ||
    !Number.isFinite(timestamp.getTime())
  )
    throw new TypeError("Valid sequenceNumber and timestamp are required");
  if (typeof platform !== "string" || !platform.trim())
    throw new TypeError("platform is required");
  const result = await executeProcedure("dbo.automatedcalls_Status", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      PhoneE164: { type: sql.VarChar(16), val: phoneE164 },
      CallStatus: { type: sql.VarChar(20), val: callStatus },
      SequenceNumber: { type: sql.Int, val: sequenceNumber },
      Timestamp: { type: sql.DateTime2(3), val: timestamp },
      CallDurationSeconds: { type: sql.Int, val: callDurationSeconds },
      SipResponseCode: { type: sql.Int, val: sipResponseCode },
      ParametersJSON: {
        type: sql.NVarChar(sql.MAX),
        val: JSON.stringify(parameters ?? {}),
      },
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });
  const context = result.recordset?.[0];
  if (!context)
    throw new Error("automatedcalls_Status did not return a result");
  return context;
}

export async function recordAutomatedCallRecordingStatus(
  {
    callId,
    providerCallId,
    accountSid,
    recordingSid,
    recordingStatus,
    recordingUrl = null,
    durationSeconds = null,
    channels = null,
    recordingStartTime = null,
    recordingSource = null,
    recordingTrack = null,
    parameters,
  },
  { platform = "AutoCallDB", timeoutMs, transaction } = {},
) {
  if (!Number.isInteger(callId) || callId <= 0 || callId > 2147483647)
    throw new TypeError("callId must be a positive SQL integer");
  for (const [value, prefix] of [
    [providerCallId, "CA"],
    [accountSid, "AC"],
    [recordingSid, "RE"],
  ]) {
    if (
      typeof value !== "string" ||
      !new RegExp(`^${prefix}[0-9a-fA-F]{32}$`).test(value)
    )
      throw new TypeError(`A valid ${prefix} SID is required`);
  }
  if (
    !["in-progress", "completed", "absent", "failed"].includes(recordingStatus)
  )
    throw new TypeError("Unsupported recording status");
  if (typeof platform !== "string" || !platform.trim())
    throw new TypeError("platform is required");
  if (
    durationSeconds !== null &&
    (!Number.isInteger(durationSeconds) ||
      durationSeconds < 0 ||
      durationSeconds > 2147483647)
  )
    throw new TypeError("Invalid recording duration");
  if (channels !== null && ![1, 2].includes(channels))
    throw new TypeError("Invalid recording channels");
  if (
    recordingStartTime !== null &&
    (!(recordingStartTime instanceof Date) ||
      !Number.isFinite(recordingStartTime.getTime()))
  )
    throw new TypeError("Invalid recording start time");
  if (
    recordingUrl !== null &&
    (typeof recordingUrl !== "string" || recordingUrl.length > 2048)
  )
    throw new TypeError("Invalid recording URL");
  if (
    recordingSource !== null &&
    (typeof recordingSource !== "string" || recordingSource.length > 100)
  )
    throw new TypeError("Invalid recording source");
  if (
    recordingTrack !== null &&
    !["inbound", "outbound", "both"].includes(recordingTrack)
  )
    throw new TypeError("Invalid recording track");
  const result = await executeProcedure("dbo.automatedcalls_RecordingStatus", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      AccountSid: { type: sql.VarChar(34), val: accountSid },
      RecordingSid: { type: sql.VarChar(34), val: recordingSid },
      RecordingStatus: { type: sql.VarChar(20), val: recordingStatus },
      RecordingUrl: { type: sql.NVarChar(2048), val: recordingUrl },
      DurationSeconds: { type: sql.Int, val: durationSeconds },
      Channels: { type: sql.TinyInt, val: channels },
      RecordingStartTime: { type: sql.DateTime2(3), val: recordingStartTime },
      RecordingSource: { type: sql.VarChar(100), val: recordingSource },
      RecordingTrack: { type: sql.VarChar(20), val: recordingTrack },
      ParametersJSON: {
        type: sql.NVarChar(sql.MAX),
        val: JSON.stringify(parameters ?? {}),
      },
    },
    platform,
    timeoutMs,
    transaction,
    strictPlatform: true,
  });
  const context = result.recordset?.[0];
  if (!context)
    throw new Error("automatedcalls_RecordingStatus did not return a result");
  return context;
}
