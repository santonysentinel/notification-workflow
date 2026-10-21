import sql from "mssql";
import { executeProcedure } from "./sqlserver.js";

export async function addToAutomatedCallQueue(
  {
    activeAlarmId,
    oid,
    flowId,
    variables,
    enqueueIdempotencyKey,
    callStatus,
    priority,
    availableAt,
    maxAttempts,
    options,
  },
  { platform, timeoutMs, transaction } = {},
) {
  if (!Number.isInteger(activeAlarmId) || activeAlarmId <= 0) {
    throw new TypeError("activeAlarmId must be a positive integer");
  }

  if (typeof oid !== "string" || !oid.trim() || oid.length > 20) {
    throw new TypeError(
      "oid must be a non-empty string of at most 20 characters",
    );
  }

  if (!Number.isInteger(flowId) || flowId <= 0) {
    throw new TypeError("flowId must be a positive integer");
  }

  if (!variables || typeof variables !== "object" || Array.isArray(variables)) {
    throw new TypeError("variables must be a JSON object");
  }

  if (
    typeof enqueueIdempotencyKey !== "string" ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      enqueueIdempotencyKey,
    ) ||
    enqueueIdempotencyKey === "00000000-0000-0000-0000-000000000000"
  ) {
    throw new TypeError("enqueueIdempotencyKey must be a nonzero UUID");
  }

  const params = {
    ActiveAlarmID: { type: sql.Int, val: activeAlarmId },
    OID: { type: sql.VarChar(20), val: oid },
    FlowId: { type: sql.Int, val: flowId },
    VariablesJSON: {
      type: sql.NVarChar(sql.MAX),
      val: JSON.stringify(
        Object.fromEntries(
          Object.entries(variables).sort(([left], [right]) =>
            left < right ? -1 : left > right ? 1 : 0,
          ),
        ),
      ),
    },
    EnqueueIdempotencyKey: {
      type: sql.UniqueIdentifier,
      val: enqueueIdempotencyKey,
    },
  };
  if (options !== undefined) {
    if (
      !options ||
      typeof options !== "object" ||
      Array.isArray(options) ||
      Object.keys(options).length !== 2 ||
      !Number.isInteger(options.historyId) ||
      options.historyId <= 0 ||
      options.historyId > 2147483647 ||
      typeof options.postRecordingTranscript !== "boolean"
    ) {
      throw new TypeError(
        "options requires a positive historyId and boolean postRecordingTranscript",
      );
    }
    params.OptionsJSON = {
      type: sql.NVarChar(sql.MAX),
      val: JSON.stringify({
        historyId: options.historyId,
        postRecordingTranscript: options.postRecordingTranscript,
      }),
    };
  }

  const optionalParams = {
    CallStatus: { type: sql.Int, val: callStatus },
    Priority: { type: sql.Int, val: priority },
    AvailableAt: { type: sql.DateTime2(3), val: availableAt },
    MaxAttempts: { type: sql.Int, val: maxAttempts },
  };

  for (const [name, spec] of Object.entries(optionalParams)) {
    if (spec.val !== undefined) {
      params[name] = spec;
    }
  }

  const result = await executeProcedure("dbo.activealarms_call_InsertJob", {
    params,
    platform,
    timeoutMs,
    transaction,
  });

  const systemId = result.recordset?.[0]?.SystemID;
  if (systemId == null) {
    throw new Error("activealarms_call_InsertJob did not return SystemID");
  }

  return systemId;
}
