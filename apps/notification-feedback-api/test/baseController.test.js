import sinon from 'sinon';
import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import baseController, {
  __setAuthForTests,
  __resetAuthForTests
} from '../app/controllers/baseController.js';
import { Errors } from '../configure/errors.js';

const buildRes = () => {
  return {
    statusCode: undefined,
    status: undefined,
    body: undefined,
    send(payload) {
      this.body = payload;
    }
  };
};

describe('baseController', () => {
  let authorizeStub;

  beforeEach(() => {
    authorizeStub = sinon.stub().resolves({
      sessionvalid: true,
      status: 200
    });
    __setAuthForTests(authorizeStub);
  });

  afterEach(() => {
    sinon.restore();
    __resetAuthForTests();
  });

  it('invokes the route controller after successful authorization', async () => {
    const req = { uuid: 'req-1' };
    const res = buildRes();
    const routeController = sinon.stub().resolves();

    await baseController(req, res, 42, routeController);

    expect(authorizeStub.calledOnceWithExactly(req, res, 42)).to.be.true;
    expect(routeController.calledOnceWithExactly(req, res)).to.be.true;
    expect(res.statusCode).to.be.undefined;
  });

  it('maps 401 authorization errors to FOUR_ZERO_ONE', async () => {
    authorizeStub.rejects({ ...Errors.FOUR_ZERO_ONE, status_code: 401 });

    const req = { uuid: 'req-2' };
    const res = buildRes();
    const routeController = sinon.stub();

    await baseController(req, res, 11, routeController);

    expect(routeController.called).to.be.false;
    expect(res.statusCode).to.equal(401);
    expect(res.body).to.deep.equal(Errors.FOUR_ZERO_ONE);
  });

  it('maps 403 authorization errors to FOUR_ZERO_THREE', async () => {
    authorizeStub.rejects({ ...Errors.FOUR_ZERO_THREE, status_code: 403 });

    const req = { uuid: 'req-3' };
    const res = buildRes();
    const routeController = sinon.stub();

    await baseController(req, res, 13, routeController);

    expect(routeController.called).to.be.false;
    expect(res.statusCode).to.equal(403);
    expect(res.body).to.deep.equal(Errors.FOUR_ZERO_THREE);
  });

  it('maps unexpected authorization errors to FIVE_ZERO_ZERO with propagated status', async () => {
    authorizeStub.rejects({ ...Errors.FIVE_ZERO_ZERO, status_code: 418 });

    const req = { uuid: 'req-4' };
    const res = buildRes();
    const routeController = sinon.stub();

    await baseController(req, res, 99, routeController);

    expect(routeController.called).to.be.false;
    expect(res.statusCode).to.equal(418);
    expect(res.body).to.deep.equal({
      ...Errors.FIVE_ZERO_ZERO,
      status_code: 418
    });
  });

  it('falls back to generic 500 when error lacks status_code', async () => {
    authorizeStub.rejects(new Error('Unknown failure'));

    const req = { uuid: 'req-5' };
    const res = buildRes();
    const routeController = sinon.stub();

    await baseController(req, res, 77, routeController);

    expect(routeController.called).to.be.false;
    expect(res.status).to.equal(500);
    expect(res.body).to.deep.equal(Errors.FIVE_ZERO_ZERO);
  });
});
