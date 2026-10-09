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
  const validJob = {
    activeAlarmId: 123,
    oid: 'client-1',
    flowId: 5,
    variables: { OffenderName: 'Alex' },
    enqueueIdempotencyKey: 'd148c0a7-7c0b-822a-982b-2a637f9a2776'
  };

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
    ['nonpositive alarm ID', { ...validJob, activeAlarmId: 0 }],
    ['nonpositive flow ID', { ...validJob, flowId: 0 }],
    ['missing variable map', { ...validJob, variables: undefined }],
    ['null variable map', { ...validJob, variables: null }],
    ['array variable map', { ...validJob, variables: [] }],
    ['string variable map', { ...validJob, variables: '{}' }],
    ['missing enqueue key', { ...validJob, enqueueIdempotencyKey: undefined }],
    ['invalid enqueue key', { ...validJob, enqueueIdempotencyKey: 'invalid' }],
    [
      'zero enqueue key',
      { ...validJob, enqueueIdempotencyKey: '00000000-0000-0000-0000-000000000000' }
    ],
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

  it('returns 409 for a conflicting enqueue snapshot', async () => {
    addToAutomatedCallQueue.rejects(
      Object.assign(new Error('Enqueue key conflicts with the original request snapshot.'), {
        httpStatus: 500,
        sqlState: { number: 51001 }
      })
    );
    const response = await request(app).post('/api/v1/autocall').send(validJob);
    expect(response.status).to.equal(409);
    expect(addToAutomatedCallQueue.calledOnce).to.be.true;
  });

  it('binds the snapshot and UUID through the real repository without SQL access', async () => {
    const { initialize } = await import('../../../pkg/repository/sqlserver.js');
    const repository = await import('../../../pkg/repository/activeAlarmParserRepository.js');
    const { default: sql } = await import('mssql');
    const pools = await initialize();
    const original = pools.AUTOCALLDB;
    const fakeRequest = {
      input: sinon.stub(),
      execute: sinon.stub().resolves({ recordset: [{ SystemID: 42 }] })
    };
    try {
      pools.AUTOCALLDB = Promise.resolve({ request: () => fakeRequest });
      const variables = { OffenderName: 'Alex & Co', AlarmText: 'Low battery' };
      expect(
        await repository.addToAutomatedCallQueue(
          { ...validJob, variables },
          { platform: 'AutoCallDB' }
        )
      ).to.equal(42);
      expect(fakeRequest.execute.calledOnceWithExactly('dbo.activealarms_call_InsertJob')).to.be
        .true;
      const bindings = Object.fromEntries(
        fakeRequest.input.args.map(([name, type, value]) => [name, { type, value }])
      );
      expect(bindings.VariablesJSON.type).to.deep.equal(sql.NVarChar(sql.MAX));
      expect(bindings.VariablesJSON.value).to.equal(
        '{"AlarmText":"Low battery","OffenderName":"Alex & Co"}'
      );
      expect(bindings.EnqueueIdempotencyKey).to.deep.equal({
        type: sql.UniqueIdentifier,
        value: validJob.enqueueIdempotencyKey
      });
    } finally {
      if (original === undefined) delete pools.AUTOCALLDB;
      else pools.AUTOCALLDB = original;
    }
  });
});
