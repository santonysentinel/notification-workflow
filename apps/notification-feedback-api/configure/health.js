import nconf from 'nconf';

const startTimestamp = Date.now();

const appState = {
  status: 'initializing',
  ready: false,
  readySince: null,
  shuttingDown: false
};

const dependencyState = {};

function ensureDependency(name) {
  if (!dependencyState[name]) {
    dependencyState[name] = {
      name,
      status: 'pending',
      ready: false,
      attempts: 0,
      lastCheckedAt: null,
      lastSuccessAt: null,
      lastFailureAt: null,
      lastError: null,
      metadata: {}
    };
  }
  return dependencyState[name];
}

function serializeError(error) {
  if (!error) return null;
  if (typeof error === 'string') {
    return { message: error };
  }

  const { message, stack, code, statusCode, status, name } = error;
  return {
    message: message || String(error),
    code: code || statusCode || status || null,
    name: name || undefined,
    stack
  };
}

function recomputeReadiness() {
  const dependencies = Object.values(dependencyState);
  const allRegistered = dependencies.length > 0;
  const allReady = allRegistered && dependencies.every((dep) => dep.ready);

  if (appState.shuttingDown) {
    appState.status = 'shutting_down';
    appState.ready = false;
    appState.readySince = null;
    return;
  }

  if (allReady) {
    if (!appState.ready) {
      appState.ready = true;
      appState.status = 'ready';
      appState.readySince = new Date().toISOString();
    }
  } else {
    appState.ready = false;
    appState.status = 'initializing';
    appState.readySince = null;
  }
}

export function registerDependency(name, metadata = {}) {
  const entry = ensureDependency(name);
  entry.metadata = { ...entry.metadata, ...metadata };
  entry.registeredAt = entry.registeredAt || new Date().toISOString();
  return entry;
}

export function recordDependencyAttempt(name) {
  const entry = ensureDependency(name);
  entry.attempts += 1;
  entry.status = 'checking';
  entry.lastCheckedAt = new Date().toISOString();
  return entry.attempts;
}

export function recordDependencySuccess(name) {
  const entry = ensureDependency(name);
  entry.status = 'ready';
  entry.ready = true;
  entry.lastSuccessAt = new Date().toISOString();
  entry.lastError = null;
  recomputeReadiness();
}

export function recordDependencyFailure(name, error) {
  const entry = ensureDependency(name);
  entry.status = 'error';
  entry.ready = false;
  entry.lastFailureAt = new Date().toISOString();
  entry.lastError = serializeError(error);
  recomputeReadiness();
}

export function markAppReady() {
  if (!appState.ready) {
    appState.ready = true;
    appState.status = 'ready';
    appState.readySince = new Date().toISOString();
  }
}

export function markAppNotReady() {
  appState.ready = false;
  appState.status = appState.shuttingDown ? 'shutting_down' : 'initializing';
  appState.readySince = null;
}

export function markAppShuttingDown() {
  appState.shuttingDown = true;
  appState.status = 'shutting_down';
  appState.ready = false;
  appState.readySince = null;
}

export function getHealthSnapshot() {
  const now = new Date();
  const dependencies = Object.fromEntries(
    Object.entries(dependencyState).map(([name, entry]) => [
      name,
      {
        ...entry,
        uptimeMs: entry.ready && entry.lastSuccessAt ? now - new Date(entry.lastSuccessAt) : null
      }
    ])
  );

  return {
    timestamp: now.toISOString(),
    version: nconf.get('version') || 'unknown',
    ready: appState.ready && Object.values(dependencyState).every((dep) => dep.ready),
    app: {
      ...appState,
      uptimeMs: now.getTime() - startTimestamp,
      startedAt: new Date(startTimestamp).toISOString()
    },
    dependencies
  };
}

export function getLivenessSnapshot() {
  const now = new Date();
  return {
    timestamp: now.toISOString(),
    version: nconf.get('version') || 'unknown',
    status: appState.shuttingDown ? 'shutting_down' : 'alive',
    uptimeMs: now.getTime() - startTimestamp,
    startedAt: new Date(startTimestamp).toISOString()
  };
}

export function isAppReady() {
  return appState.ready && Object.values(dependencyState).every((dep) => dep.ready);
}

export function __resetHealthStateForTests() {
  appState.status = 'initializing';
  appState.ready = false;
  appState.readySince = null;
  appState.shuttingDown = false;

  Object.keys(dependencyState).forEach((key) => {
    delete dependencyState[key];
  });
}
