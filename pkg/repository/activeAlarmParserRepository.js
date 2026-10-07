import sql from 'mssql';
import { executeProcedure } from './sqlserver.js';

export async function addToAutomatedCallQueue(
  { activeAlarmId, oid, flowId, callStatus, priority, availableAt, maxAttempts },
  { platform, timeoutMs, transaction } = {}
) {
  if (!Number.isInteger(activeAlarmId)) {
    throw new TypeError('activeAlarmId must be an integer');
  }

  if (typeof oid !== 'string' || !oid.trim() || oid.length > 20) {
    throw new TypeError('oid must be a non-empty string of at most 20 characters');
  }

  if (!Number.isInteger(flowId)) {
    throw new TypeError('flowId must be an integer');
  }

  const params = {
    ActiveAlarmID: { type: sql.Int, val: activeAlarmId },
    OID: { type: sql.VarChar(20), val: oid },
    FlowId: { type: sql.Int, val: flowId }
  };

  const optionalParams = {
    CallStatus: { type: sql.Int, val: callStatus },
    Priority: { type: sql.Int, val: priority },
    AvailableAt: { type: sql.DateTime2(3), val: availableAt },
    MaxAttempts: { type: sql.Int, val: maxAttempts }
  };

  for (const [name, spec] of Object.entries(optionalParams)) {
    if (spec.val !== undefined) {
      params[name] = spec;
    }
  }

  const result = await executeProcedure('dbo.activealarms_call_InsertJob', {
    params,
    platform,
    timeoutMs,
    transaction
  });

  const systemId = result.recordset?.[0]?.SystemID;
  if (systemId == null) {
    throw new Error('activealarms_call_InsertJob did not return SystemID');
  }

  return systemId;
}