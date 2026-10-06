import express from 'express';
import request from 'supertest';
import { describe, it, before, beforeEach } from 'mocha';
import { expect } from 'chai';
import {
  __resetHealthStateForTests,
  getHealthSnapshot,
  getLivenessSnapshot,
  markAppReady,
  markAppNotReady,
  markAppShuttingDown,
  registerDependency,
  recordDependencySuccess,
  recordDependencyFailure
} from '../configure/health.js';

process.env.NODE_ENV = 'test';

const buildHealthApp = () => {
  const app = express();

  app.get('/health/live', (_req, res) => {
    const snapshot = getLivenessSnapshot();
    const statusCode = snapshot.status === 'alive' ? 200 : 503;
    res.status(statusCode).json(snapshot);
  });

  app.get('/health/ready', (_req, res) => {
    const snapshot = getHealthSnapshot();
    res.status(snapshot.ready ? 200 : 503).json(snapshot);
  });

  return app;
};

describe('Health endpoints', function () {
  this.timeout(5000);

  let app;

  before(() => {
    app = buildHealthApp();
  });

  beforeEach(() => {
    __resetHealthStateForTests();
  });

  it('returns 503 when the app is not ready', async () => {
    markAppNotReady();

    const res = await request(app).get('/health/ready').expect(503);

    expect(res.body.ready).to.be.false;
    expect(res.body.app).to.include({ status: 'initializing' });
  });

  it('returns 200 when dependencies are ready', async () => {
    const dependencyName = 'health-ready-dependency';
    registerDependency(dependencyName, { component: 'document-service' });
    recordDependencySuccess(dependencyName);
    markAppReady();

    const res = await request(app).get('/health/ready').expect(200);

    expect(res.body.ready).to.be.true;
    expect(res.body.dependencies).to.have.property(dependencyName);
    expect(res.body.dependencies[dependencyName].status).to.equal('ready');
  });

  it('returns 503 when a dependency reports an error', async () => {
    const dependencyName = 'health-failure-dependency';
    registerDependency(dependencyName, { component: 'document-service' });
    recordDependencyFailure(dependencyName, new Error('Downstream unavailable'));
    markAppNotReady();

    const res = await request(app).get('/health/ready').expect(503);

    expect(res.body.ready).to.be.false;
    expect(res.body.dependencies[dependencyName].status).to.equal('error');
    expect(res.body.dependencies[dependencyName].lastError).to.include({ message: 'Downstream unavailable' });
  });

  it('returns 200 for liveness when the service is alive', async () => {
    markAppReady();

    const res = await request(app).get('/health/live').expect(200);

    expect(res.body.status).to.equal('alive');
  });

  it('returns 503 for liveness when shutting down', async () => {
    markAppShuttingDown();

    const res = await request(app).get('/health/live').expect(503);

    expect(res.body.status).to.equal('shutting_down');
  });
});
