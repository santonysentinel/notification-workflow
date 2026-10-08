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
  {
    callId,
    providerCallId,
    phoneE164,
    startedCallStatus,
    pendingCallStatus = null,
    openingStep = null,
  },
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
  for (const status of [startedCallStatus, pendingCallStatus]) {
    if (
      status !== null &&
      (!Number.isInteger(status) || status <= 0 || status > 2147483647)
    ) {
      throw new TypeError("call statuses must be positive SQL integers");
    }
  }
  if (
    startedCallStatus == null ||
    typeof platform !== "string" ||
    !platform.trim()
  ) {
    throw new TypeError("startedCallStatus and platform are required");
  }

  const result = await executeProcedure("dbo.automatedcalls_Start", {
    params: {
      CallId: { type: sql.Int, val: callId },
      ProviderCallId: { type: sql.VarChar(200), val: providerCallId },
      PhoneE164: { type: sql.VarChar(16), val: phoneE164 },
      StartedCallStatus: { type: sql.Int, val: startedCallStatus },
      PendingCallStatus: { type: sql.Int, val: pendingCallStatus },
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
