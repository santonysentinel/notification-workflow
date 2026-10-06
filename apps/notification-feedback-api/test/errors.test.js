import sinon from 'sinon';
import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import { Errors, ApiError, makeError, sendError } from '../configure/errors.js';

describe('configure/errors', () => {
  let clock;

  beforeEach(() => {
    clock = sinon.useFakeTimers(new Date('2025-11-14T12:00:00.000Z'));
  });

  afterEach(() => {
    sinon.restore();
  });

  it('ApiError captures provided fields and defaults', () => {
    const err = new ApiError({
      statusCode: 422,
      message: 'Invalid payload',
      errorCode: 2000,
      errorField: 'email',
      details: { reason: 'format' },
      cause: new Error('root cause')
    });

    expect(err).to.include({
      statusCode: 422,
      message: 'Invalid payload',
      errorCode: 2000,
      errorField: 'email'
    });
    expect(err.details).to.deep.equal({ reason: 'format' });
    expect(err.cause).to.be.instanceOf(Error);
  });

  it('ApiError.toJSON emits normalized payload including timestamp and trace info', () => {
    const err = new ApiError({
      statusCode: 404,
      message: 'Not here',
      errorCode: 4040
    });

    const json = err.toJSON({ traceId: 'trace-123', path: '/api/path' });

    expect(json).to.deep.equal({
      status_code: 404,
      message: 'Not here',
      error_code: 4040,
      error_field: null,
      trace_id: 'trace-123',
      path: '/api/path',
      details: null,
      timestamp: new Date().toISOString()
    });
  });

  it('makeError creates ApiError with merged options', () => {
    const err = makeError(400, 'Bad request', {
      errorCode: 123,
      errorField: 'field',
      details: { issue: 'missing' },
      cause: new Error('cause')
    });

    expect(err).to.be.instanceOf(ApiError);
    expect(err.statusCode).to.equal(400);
    expect(err.errorCode).to.equal(123);
    expect(err.errorField).to.equal('field');
    expect(err.details).to.deep.equal({ issue: 'missing' });
    expect(err.cause.message).to.equal('cause');
  });

  it('sendError writes response with serialized error', () => {
    const statusSpy = sinon.stub();
    const jsonSpy = sinon.stub();
    statusSpy.returns({ json: jsonSpy });

    const res = {
      status: statusSpy
    };

    sendError(res, 401, 'Unauthorized', {
      errorCode: 99,
      errorField: 'auth',
      details: { reason: 'token' },
      traceId: 'trace-1',
      path: '/api/secure'
    });

    expect(statusSpy.calledOnceWithExactly(401)).to.be.true;
    expect(jsonSpy.calledOnce).to.be.true;
    const payload = jsonSpy.firstCall.args[0];
    expect(payload).to.deep.equal({
      status_code: 401,
      message: 'Unauthorized',
      error_code: 99,
      error_field: 'auth',
      trace_id: 'trace-1',
      path: '/api/secure',
      details: { reason: 'token' },
      timestamp: new Date().toISOString()
    });
  });

  it('exports canonical Errors set', () => {
    expect(Errors.FOUR_ZERO_ZERO).to.deep.equal({
      status_code: 400,
      message: 'Missing required field(s)',
      error_code: 1000,
      error_field: null
    });

    expect(Errors.FIVE_ZERO_ZERO.status_code).to.equal(500);
  });
});
