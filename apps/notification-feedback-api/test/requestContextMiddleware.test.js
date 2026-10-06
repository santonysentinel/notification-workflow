import sinon from 'sinon';
import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import requestContextMiddleware from '../app/middleware/requestContextMiddleware.js';
import { Logger as logger } from '../configure/loggers.js';

const buildReqRes = (reqOverrides = {}, resOverrides = {}) => {
  const events = {};
  const req = {
    headers: {},
    query: {},
    method: 'GET',
    originalUrl: '/test',
    ...reqOverrides,
    headers: { ...(reqOverrides.headers || {}) }
  };

  const res = {
    headers: {},
    statusCode: 200,
    writableFinished: true,
    setHeader: sinon.spy((name, value) => {
      res.headers[name.toLowerCase()] = value;
    }),
    get: sinon.spy((name) => res.headers[name.toLowerCase()]),
    on: sinon.spy((event, handler) => {
      events[event] = handler;
    }),
    ...resOverrides
  };

  return { req, res, events };
};

describe('requestContextMiddleware', () => {
  let childStub;
  let infoStub;
  let warnStub;

  beforeEach(() => {
    infoStub = sinon.stub();
    warnStub = sinon.stub();
    childStub = sinon.stub(logger, 'child').returns({
      info: infoStub,
      warn: warnStub
    });
  });

  afterEach(() => {
    sinon.restore();
  });

  it('generates a trace id when none is supplied and attaches headers/context', () => {
    const middleware = requestContextMiddleware();
    const next = sinon.stub();
    const { req, res } = buildReqRes();

    middleware(req, res, next);

    expect(req.headers['x-request-id']).to.be.a('string').and.to.have.length.greaterThan(10);
    expect(res.setHeader.calledWith('x-request-id', req.headers['x-request-id'])).to.be.true;
    expect(req.uuid).to.equal(req.headers['x-request-id']);
    expect(req.context.traceId).to.equal(req.headers['x-request-id']);
    expect(next.calledOnce).to.be.true;
    expect(childStub.calledOnce).to.be.true;
  });


  it('logs on finish and warns on aborted requests', () => {
    const middleware = requestContextMiddleware();
    const next = sinon.stub();
    const { req, res, events } = buildReqRes();

    middleware(req, res, next);

    // Simulate successful completion
    res.statusCode = 201;
    res.headers['content-length'] = '42';
    events.finish();
    expect(infoStub.calledWithMatch('request.completed', {
      statusCode: 201,
      contentLength: '42'
    })).to.be.true;

    // Simulate aborted connection
    res.statusCode = 500;
    res.writableFinished = false;
    events.close();
    expect(warnStub.calledWithMatch('request.aborted', { statusCode: 500 })).to.be.true;
  });
});
