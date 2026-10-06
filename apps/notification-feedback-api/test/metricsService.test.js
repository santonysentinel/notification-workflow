import sinon from 'sinon';
import { describe, it, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import os from 'node:os';
import * as metrics from '../app/services/metrics.js';

const ORIGINAL_ENV = {
  SERVICE_NAME: process.env.SERVICE_NAME,
  NODE_ENV: process.env.NODE_ENV,
  HOSTNAME: process.env.HOSTNAME
};

const restoreEnv = () => {
  Object.entries(ORIGINAL_ENV).forEach(([key, value]) => {
    if (value === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = value;
    }
  });
};

describe('metrics service', () => {
  beforeEach(() => {
    metrics.close();
    sinon.restore();
    process.env.SERVICE_NAME = 'test-service';
    process.env.NODE_ENV = 'test-env';
    sinon.stub(os, 'hostname').returns('test-host');
  });

  afterEach(() => {
    metrics.close();
    sinon.restore();
    restoreEnv();
  });

  it('initializes StatsD with merged global tags and stores client', () => {
    const client = metrics.initializeStatsD({
      mock: true,
      globalTags: { feature: 'documents' }
    });

    expect(client).to.be.an('object');
    expect(metrics.getStatsClient()).to.equal(client);
    expect(client.globalTags).to.include.members([
      'feature:documents',
      'service:test-service',
      'env:test-env',
      'instance:test-host'
    ]);
  });

  it('initializes from nconf-style config object', () => {
    const getStub = sinon.stub();
    getStub.withArgs('statsD:host').returns('metrics-host');
    getStub.withArgs('statsD:port').returns(18125);
    getStub.withArgs('statsD:protocol').returns('tcp');
    getStub.withArgs('statsD:prefix').returns('custom.');
    getStub.withArgs('statsD:suffix').returns('.suffix');
    getStub.withArgs('statsD:telegraf').returns(true);
    getStub.withArgs('statsD:mock').returns(true);
    getStub.withArgs('statsD:maxBufferSize').returns(512);
    getStub.withArgs('statsD:bufferFlushInterval').returns(250);
    getStub.withArgs('statsD:globalTags').returns({
      service: 'config-service',
      custom: 'tag'
    });

    const nconfLike = { get: getStub };

    const client = metrics.initializeFromConfig(nconfLike);

    expect(client.host).to.equal('metrics-host');
    expect(client.port).to.equal(18125);
    expect(client.globalTags).to.include.members([
      'service:config-service',
      'custom:tag'
    ]);
  });

  it('throws when requiring client before initialization', () => {
    metrics.close();
    expect(() => metrics.requireStatsClient()).to.throw('StatsD client not initialized. Call initializeStatsD() first.');
  });

  it('delegates increment/gauge/histogram/timing to StatsD client', () => {
    const client = metrics.initializeStatsD({ mock: true });

    const incrementSpy = sinon.spy(client, 'increment');
    const gaugeSpy = sinon.spy(client, 'gauge');
    const histogramSpy = sinon.spy(client, 'histogram');
    const timingSpy = sinon.spy(client, 'timing');

    metrics.increment('metric.increment', 3, { region: 'us' });
    metrics.gauge('metric.gauge', 42, { region: 'us' });
    metrics.histogram('metric.histogram', 10, { region: 'us' });
    metrics.timing('metric.timing', 15, { region: 'us' });

    expect(incrementSpy.calledOnceWith('metric.increment', 3, { region: 'us' })).to.be.true;
    expect(gaugeSpy.calledOnceWith('metric.gauge', 42, { region: 'us' })).to.be.true;
    expect(histogramSpy.calledOnceWith('metric.histogram', 10, { region: 'us' })).to.be.true;
    expect(timingSpy.calledWith('metric.timing', 15, { region: 'us' })).to.be.true;
  });

  it('records elapsed time via timer helper', async () => {
    const client = metrics.initializeStatsD({ mock: true });
    const timingSpy = sinon.spy(client, 'timing');

    const endTimer = metrics.timer('metric.timer', { scope: 'test' });
    await new Promise((resolve) => setTimeout(resolve, 5));
    const elapsed = endTimer();

    expect(elapsed).to.be.a('number').that.is.greaterThan(0);
    expect(timingSpy.calledOnce).to.be.true;
    const [metricName, duration, tags] = timingSpy.firstCall.args;
    expect(metricName).to.equal('metric.timer');
    expect(tags).to.deep.equal({ scope: 'test' });
    expect(duration).to.equal(elapsed);
  });

  it('closes and clears the shared client', () => {
    const client = metrics.initializeStatsD({ mock: true });
    const closeSpy = sinon.spy(client, 'close');

    metrics.close();

    expect(closeSpy.calledOnce).to.be.true;
    expect(metrics.getStatsClient()).to.be.null;
  });
});
