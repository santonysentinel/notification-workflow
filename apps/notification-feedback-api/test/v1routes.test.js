import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import v1Routes from '../app/routes/v1/v1routes.js';

const findErrorMiddleware = () => {
  const layer = v1Routes.stack?.find((entry) => typeof entry.handle === 'function' && entry.handle.length === 4);
  if (!layer) {
    throw new Error('v1 routes error-handling middleware not found');
  }
  return layer.handle;
};

describe('v1 routes error middleware', () => {
  let errorHandler;

  beforeEach(() => {
    errorHandler = findErrorMiddleware();
  });

  afterEach(() => {
    sinon.restore();
  });

  it('responds with 413 and detailed message when Multer provides a size limit', () => {
    const res = {
      status: sinon.stub().returnsThis(),
      json: sinon.stub().returnsThis()
    };
    const next = sinon.stub();

    errorHandler({ code: 'LIMIT_FILE_SIZE', limit: 20 * 1024 * 1024 }, {}, res, next);

    expect(res.status.calledOnceWith(413)).to.be.true;
    expect(res.json.calledOnce).to.be.true;
    expect(res.json.firstCall.args[0]).to.deep.equal({
      error: 'File too large. Max allowed is 20MB.',
      limitBytes: 20 * 1024 * 1024
    });
    expect(next.notCalled).to.be.true;
  });

  it('omits the limitBytes field when Multer does not set a limit value', () => {
    const res = {
      status: sinon.stub().returnsThis(),
      json: sinon.stub().returnsThis()
    };
    const next = sinon.stub();

    errorHandler({ code: 'LIMIT_FILE_SIZE' }, {}, res, next);

    expect(res.status.calledOnceWith(413)).to.be.true;
    expect(res.json.firstCall.args[0]).to.deep.equal({
      error: 'File too large. Uploaded file exceeds configured limit.',
      limitBytes: undefined
    });
    expect(next.notCalled).to.be.true;
  });

  it('delegates to next error handler for non file-size errors', () => {
    const res = {
      status: sinon.stub().returnsThis(),
      json: sinon.stub().returnsThis()
    };
    const next = sinon.stub();
    const error = new Error('boom');

    errorHandler(error, {}, res, next);

    expect(res.status.notCalled).to.be.true;
    expect(res.json.notCalled).to.be.true;
    expect(next.calledOnceWith(error)).to.be.true;
  });
});