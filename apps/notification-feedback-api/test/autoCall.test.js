import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import express from 'express';
import request from 'supertest';
import nconf from 'nconf';

const savedDependencies = nconf.get('dependencies');
nconf.set('dependencies', { databases: {} });
let autoCallRouter;
let controller;
try {
  autoCallRouter = (await import('../app/routes/v1/autoCall.js')).default;
  controller = await import('../app/controllers/autoCallController.js');
} finally {
  if (savedDependencies === undefined) {
    nconf.clear('dependencies');
  } else {
    nconf.set('dependencies', savedDependencies);
  }
}

describe('POST /autocall', () => {
  let app;
  let addToAutomatedCallQueue;
  let authenticatedOid;
  let routing;
  let log;
  const validJob = { activeAlarmId: 123, oid: 'client-1', flowId: 5 };

  beforeEach(() => {
    authenticatedOid = undefined;
    routing = { Platform: 'TEST' };
    log = { error: sinon.stub() };
    addToAutomatedCallQueue = sinon.stub().resolves(42);
    controller.__setRepositoryForTests({ addToAutomatedCallQueue });
    app = express();
    app.use(express.json());
    app.use((req, res, next) => {
      req.context = { traceId: 'test-trace' };
      req.oid = authenticatedOid;
      req.routing = routing;
      req.log = log;
      next();
    });
    app.use('/api/v1', autoCallRouter);
  });

  afterEach(() => {
    controller.__resetRepositoryForTests();
    sinon.restore();
  });

  it('creates a queue job and returns its identity', async () => {
    const response = await request(app).post('/api/v1/autocall').send(validJob);

    expect(response.status).to.equal(201);
    expect(response.body).to.deep.equal({ systemId: 42 });
    expect(addToAutomatedCallQueue.calledOnce).to.be.true;
    expect(addToAutomatedCallQueue.firstCall.args).to.deep.equal([
      {
        ...validJob,
        callStatus: undefined,
        priority: undefined,
        availableAt: undefined,
        maxAttempts: undefined
      },
      { platform: 'TEST' }
    ]);
  });

  it('passes optional values and converts the scheduled time to a Date', async () => {
    routing = { platform: 'SECONDARY' };
    const response = await request(app)
      .post('/api/v1/autocall')
      .send({
        ...validJob,
        callStatus: null,
        priority: 0,
        availableAt: '2026-10-08T12:00:00Z',
        maxAttempts: 3
      });

    expect(response.status).to.equal(201);
    const [job, options] = addToAutomatedCallQueue.firstCall.args;
    expect(job.callStatus).to.equal(null);
    expect(job.priority).to.equal(0);
    expect(job.availableAt.toISOString()).to.equal('2026-10-08T12:00:00.000Z');
    expect(job.maxAttempts).to.equal(3);
    expect(options).to.deep.equal({ platform: 'SECONDARY' });
  });

  it('uses middleware OID instead of a caller-supplied OID', async () => {
    authenticatedOid = 'authenticated';
    const response = await request(app).post('/api/v1/autocall').send(validJob);

    expect(response.status).to.equal(201);
    expect(addToAutomatedCallQueue.firstCall.args[0].oid).to.equal('authenticated');
  });

  it('rejects a missing JSON body', async () => {
    const response = await request(app).post('/api/v1/autocall');

    expect(response.status).to.equal(400);
    expect(addToAutomatedCallQueue.notCalled).to.be.true;
  });

  const invalidJobs = [
    ['missing fields', {}],
    ['non-integer alarm ID', { ...validJob, activeAlarmId: '123' }],
    ['missing flow ID', { ...validJob, flowId: undefined }],
    ['blank OID', { ...validJob, oid: '   ' }],
    ['missing OID', { ...validJob, oid: undefined }],
    ['overlong OID', { ...validJob, oid: 'a'.repeat(21) }],
    ['null priority', { ...validJob, priority: null }],
    ['invalid call status', { ...validJob, callStatus: '2' }],
    ['nonpositive max attempts', { ...validJob, maxAttempts: 0 }],
    ['invalid scheduled time', { ...validJob, availableAt: 'invalid' }]
  ];

  for (const [name, job] of invalidJobs) {
    it(`rejects ${name} before calling the repository`, async () => {
      const response = await request(app).post('/api/v1/autocall').send(job);

      expect(response.status).to.equal(400);
      expect(response.body.trace_id).to.equal('test-trace');
      expect(response.body.path).to.equal('/api/v1/autocall');
      expect(addToAutomatedCallQueue.notCalled).to.be.true;
    });
  }

  it('preserves repository error status and SQL details', async () => {
    addToAutomatedCallQueue.rejects(
      Object.assign(new Error('Conflict'), {
        httpStatus: 409,
        sqlState: { number: 2627 }
      })
    );
    const response = await request(app).post('/api/v1/autocall').send(validJob);

    expect(response.status).to.equal(409);
    expect(response.body.details).to.deep.equal({ sqlState: { number: 2627 } });
    expect(log.error.calledOnce).to.be.true;
  });

  it('returns 500 for an unexpected repository failure', async () => {
    addToAutomatedCallQueue.rejects(new Error('Database unavailable'));
    const response = await request(app).post('/api/v1/autocall').send(validJob);

    expect(response.status).to.equal(500);
    expect(response.body.message).to.equal('Database unavailable');
  });
});
