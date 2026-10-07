import sql from 'mssql';
import nconf from 'nconf';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

nconf.argv().env();

const CONFIG_OVERRIDE_ENV_VARS = ['PAPP_CONFIG_FILE', 'APP_CONFIG_FILE', 'CONFIG_FILE_PATH'];

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const defaultConfigPath = path.resolve(currentDir, '..', '..', 'apps', 'api', 'config.json');
let configLoaded = false;

function resolveConfigOverride(configFile) {
  if (typeof configFile === 'string' && configFile.trim()) {
    return configFile.trim();
  }

  for (const envVar of CONFIG_OVERRIDE_ENV_VARS) {
    const value = process.env[envVar];
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }
  }

  return null;
}

function ensureConfigLoaded(configFile) {
  const override = resolveConfigOverride(configFile);
  if (override) {
    const resolved = path.isAbsolute(override) ? override : path.resolve(process.cwd(), override);
    nconf.file({ file: resolved });
    configLoaded = true;
    return;
  }

  if (configLoaded) return;

  const existingDependencies = nconf.get('dependencies');
  if (existingDependencies && typeof existingDependencies === 'object') {
    configLoaded = true;
    return;
  }

  nconf.file({ file: defaultConfigPath });
  configLoaded = true;
}

// Retry knobs (env-overridable)
const sqlRetryKnobs = nconf.get('sql_retry_knobs') || {};
const MAX_RETRIES = Number(sqlRetryKnobs['max_retries'] ?? 2);
const BASE_DELAY_MS = Number(sqlRetryKnobs['base_delay_ms'] ?? 200);
const MAX_DELAY_MS = Number(sqlRetryKnobs['max_delay_ms'] ?? 5000);

const DEFAULT_POOL_SETTINGS = {
  max: 5,
  min: 0,
  idleTimeoutMillis: 30000
};

const DEFAULT_POOL_OPTIONS = {
  encrypt: true,
  trustServerCertificate: false,
  enableArithAbort: true
};

const DEFAULT_POOL_KEY = 'DEFAULT';
const Pools = Object.create(null);

let initializationPromise;

export async function initialize({ configFile, force = false } = {}) {
  if (force) {
    initializationPromise = null;
    configLoaded = false;
  }

  ensureConfigLoaded(configFile);

  if (!initializationPromise) {
    initializationPromise = setupPools();
  }

  return initializationPromise;
}

await initialize();

async function setupPools() {
  for (const key of Object.keys(Pools)) {
    delete Pools[key];
  }

  const dbConfigs = loadDatabaseConfigs();
  const entries = Object.entries(dbConfigs);

  if (!entries.length) {
    console.warn('[SQL POOL INIT] No database configs found under dependencies.databases');
    Pools[DEFAULT_POOL_KEY] = null;
    return Pools;
  }

  const normalizedEntries = entries
    .map(([name, cfg]) => [normalizePoolKey(name), cfg])
    .filter(([key]) => key);

  if (!normalizedEntries.length) {
    console.warn('[SQL POOL INIT] No valid database keys found under dependencies.databases');
    Pools[DEFAULT_POOL_KEY] = null;
    return Pools;
  }

  for (const [key, cfg] of normalizedEntries) {
    Pools[key] = createPoolPromise(key, cfg);
  }

  const defaultKey = resolveDefaultPoolKey(normalizedEntries);
  if (defaultKey) {
    Pools[DEFAULT_POOL_KEY] = Pools[defaultKey];
  } else {
    Pools[DEFAULT_POOL_KEY] = Pools[normalizedEntries[0][0]];
  }

  return Pools;
}

function loadDatabaseConfigs() {
  const dependencies = nconf.get('dependencies');
  if (!dependencies || typeof dependencies !== 'object') return {};
  const { databases } = dependencies;
  if (!databases || typeof databases !== 'object') return {};
  return databases;
}

function createPoolPromise(key, cfg) {
  const poolConfig = normalizePoolConfig(cfg);
  return new sql.ConnectionPool(poolConfig)
    .connect()
    .then((pool) => {
      console.log(`[SQL POOL READY] Connected to MSSQL - ${key}`);
      return pool;
    })
    .catch((err) => {
      console.error(`[SQL POOL ERROR] Failed to connect - ${key}`, err);
      return null;
    });
}

function normalizePoolConfig(cfg) {
  if (!cfg || typeof cfg !== 'object') {
    throw new Error('Invalid database configuration');
  }

  const { pool, options, ...credentials } = cfg;

  return {
    ...credentials,
    pool: { ...DEFAULT_POOL_SETTINGS, ...(pool || {}) },
    options: { ...DEFAULT_POOL_OPTIONS, ...(options || {}) }
  };
}

function normalizePoolKey(name) {
  return String(name || '')
    .trim()
    .toUpperCase();
}

function resolveDefaultPoolKey(entries) {
  const configuredDefault = nconf.get('dependencies:defaultDatabase');
  if (configuredDefault) {
    const normalized = normalizePoolKey(configuredDefault);
    if (entries.some(([key]) => key === normalized)) {
      return normalized;
    }
  }

  return entries[0]?.[0] ?? null;
}

export async function executeProcedure(
  procName,
  { params = {}, outputs = {}, platform, timeoutMs, transaction, strictPlatform = false } = {}
) {
  assertValidProcName(procName);

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const isTransient = (err) => {
    const code = err?.code || err?.originalError?.code;
    const name = err?.name;
    // Common transient/connection-level issues
    if (['ETIMEOUT', 'ESOCKET', 'ECONNRESET', 'ECONNREFUSED', 'ELOGIN'].includes(code)) return true;
    if (name === 'ConnectionError' || name === 'TimeoutError') return true;
    // SQL throttling (if applicable)
    const number = err?.number || err?.originalError?.number;
    if ([40501, 10928, 10929].includes(Number(number))) return true;
    // Heuristic: messages indicating timeouts
    const msg = (err?.message || '').toLowerCase();
    if (msg.includes('timeout') || msg.includes('timed out')) return true;
    return false;
  };

  const isStoredProcError = (err) => {
    // Errors originating from SQL Server procedure/statement execution
    const oe = err?.originalError;
    if (oe?.procName) return true;
    if (
      err?.name === 'RequestError' &&
      (typeof oe?.number === 'number' || typeof err?.number === 'number')
    )
      return true;
    return false;
  };

  const decorateStoredProcError = (err) => {
    if (!err || err.httpStatus) {
      return err;
    }

    const oe = err.originalError || {};
    const severity = Number(oe.class);
    const number = Number(oe.number ?? err.number);

    let status = 500;

    if (Number.isInteger(number)) {
      if (number === 2601 || number === 2627) {
        status = 409; // duplicate key
      } else if (number >= 50000 && number < 50100) {
        status = 422; // app-defined validation bucket
      }
    }

    if (status === 500 && Number.isInteger(severity)) {
      if (severity >= 11 && severity <= 16) {
        status = 400;
      } else if (severity >= 17 && severity <= 25) {
        status = 500;
      }
    }

    err.isStoredProcError = true;
    err.httpStatus = status;
    err.sqlState = {
      severity: Number.isNaN(severity) ? undefined : severity,
      number: Number.isNaN(number) ? undefined : number,
      state: oe.state,
      procedure: oe.procName,
      line: oe.lineNumber
    };

    return err;
  };

  let lastErr;
  for (let attempt = 0; attempt <= MAX_RETRIES; attempt++) {
    try {
      const key = platform ? normalizePoolKey(platform) : DEFAULT_POOL_KEY;
      const poolPromise = strictPlatform ? Pools[key] : Pools[key] || Pools[DEFAULT_POOL_KEY];
      if (!poolPromise) throw new Error(`No pool configured for platform: ${key}`);

      const pool = await poolPromise;
      if (!pool) throw new Error('MSSQL pool is not available');

      const request = transaction ? new sql.Request(transaction) : pool.request();

      if (timeoutMs != null) request.timeout = timeoutMs;

      // bind inputs (coerce undefined -> null so DB receives NULL)
      for (const [name, spec] of Object.entries(params)) {
        const hasType = spec && Object.prototype.hasOwnProperty.call(spec, 'type');
        const raw = hasType ? spec.val : (spec?.val ?? spec);
        const val = raw === undefined ? null : raw; // pass NULL to SQL for undefined
        if (hasType) {
          request.input(name, spec.type, val);
        } else {
          // Let driver infer type
          request.input(name, val);
        }
      }

      // bind outputs
      for (const [name, type] of Object.entries(outputs)) {
        request.output(name, type);
      }

      const result = await request.execute(procName);

      return {
        recordset: result.recordset ?? null,
        recordsets: result.recordsets ?? [],
        output: result.output ?? {},
        returnValue: result.returnValue,
        rowsAffected: result.rowsAffected ?? []
      };
    } catch (err) {
      lastErr = err;
      const oe = err?.originalError;
      // Log once per attempt
      console.error('[SQL PROC ERROR]', {
        proc: procName,
        attempt: `${attempt + 1}/${MAX_RETRIES + 1}`,
        code: err?.code || oe?.code,
        number: oe?.number,
        state: oe?.state,
        class: oe?.class,
        line: oe?.lineNumber,
        server: oe?.serverName,
        procName: oe?.procName,
        message: err?.message
      });

      // If error is from stored proc logic or a transaction is provided, do not retry
      if (transaction || isStoredProcError(err)) throw decorateStoredProcError(err);

      if (attempt < MAX_RETRIES && isTransient(err)) {
        // Exponential backoff; timeouts/unreachability sleep a bit longer
        const base = BASE_DELAY_MS * Math.pow(2, attempt);
        const longFactor = ['ETIMEOUT', 'ECONNREFUSED'].includes(err?.code) ? 2 : 1;
        const backoff = Math.min(MAX_DELAY_MS, base * longFactor);
        const jitter = Math.floor(Math.random() * (backoff * 0.2));
        const delay = backoff + jitter;
        await sleep(delay);
        continue; // retry
      }

      // Not retryable or max attempts reached
      throw err;
    }
  }

  // Should not reach; throw last error just in case
  throw lastErr;
}

function assertValidProcName(name) {
  if (!/^[\[\]A-Za-z0-9_.]+$/.test(name)) {
    throw new Error(`Invalid stored procedure name: ${name}`);
  }
}
