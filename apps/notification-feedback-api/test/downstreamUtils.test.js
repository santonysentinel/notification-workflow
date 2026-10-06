import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import nconf from 'nconf';
import { relayDownstreamResponse, resolveServiceBase } from '../app/utils/downstream.js';

describe('downstream utilities', () => {
  let originalValue;

  beforeEach(() => {
    sinon.restore();
    originalValue = nconf.get('microservices:test_service');
  });

  afterEach(() => {
    sinon.restore();
    if (originalValue !== undefined) {
      nconf.set('microservices:test_service', originalValue);
    } else {
      nconf.clear('microservices:test_service');
    }
  });

  it('relays downstream response with content', () => {
    const res = {
      set: sinon.stub(),
      status: sinon.stub().returnsThis(),
      send: sinon.stub(),
      end: sinon.stub()
    };

    relayDownstreamResponse(res, {
      status: 204,
      headers: { 'content-type': 'application/json' },
      data: { ok: true }
    });

    expect(res.set.calledOnceWith('content-type', 'application/json')).to.be.true;
    expect(res.status.calledOnceWith(204)).to.be.true;
    expect(res.send.calledOnceWith({ ok: true })).to.be.true;
    expect(res.end.called).to.be.false;
  });

  it('relays downstream response without body', () => {
    const res = {
      set: sinon.stub(),
      status: sinon.stub().returnsThis(),
      send: sinon.stub(),
      end: sinon.stub()
    };

    relayDownstreamResponse(res, {
      status: 204,
      headers: {},
      data: undefined
    });

    expect(res.set.called).to.be.false;
    expect(res.status.calledOnceWith(204)).to.be.true;
    expect(res.send.called).to.be.false;
    expect(res.end.calledOnce).to.be.true;
  });

  it('returns configured service base URL', () => {
    nconf.set('microservices:test_service', 'http://service.local');
    const url = resolveServiceBase('test_service', 'Test service');
    expect(url).to.equal('http://service.local');
  });

  it('throws when service base URL is missing', () => {
    nconf.clear('microservices:test_service');
    expect(() => resolveServiceBase('test_service', 'Test service')).to.throw(
      'Test service base URL is not configured'
    );
  });
});
