import http from 'node:http';
import express from 'express';
import request from 'supertest';
import { describe, it, before, after, beforeEach, afterEach } from 'mocha';
import { expect } from 'chai';
import sinon from 'sinon';
import nconf from 'nconf';
import routes from '../app/routes/routes.js';
import requestContextMiddleware from '../app/middleware/requestContextMiddleware.js';

process.env.NODE_ENV = 'test';

const buildApp = () => {
  const app = express();
  app.use(requestContextMiddleware());
  app.use(express.json());
  routes(app);

  app.use((err, _req, res, _next) => {
    const status = err?.status || err?.statusCode || 500;
    res.status(status).json({ error: err?.message || 'Internal Error' });
  });

  return app;
};

const authHeader = {
  Authorization: 'Bearer test-token'
};

describe('Housekeeping participant endpoints', () => {
  let app;
  let downstreamServer;
  let downstreamResponse;
  let lastDownstreamRequest;
  let overrideBaseUrl;
  let originalBaseUrl;

  before(async () => {
    app = buildApp();

    downstreamResponse = {
      statusCode: 200,
      body: {
        oid: 'ID901983',
        po_group_num: 'QA',
        is_chat_enabled: true,
        is_schedule_requests_enabled: true,
        is_document_upload_enabled: true
      }
    };

    downstreamServer = http.createServer((req, res) => {
      const chunks = [];
      req.on('data', (chunk) => chunks.push(chunk));
      req.on('end', () => {
        lastDownstreamRequest = {
          method: req.method,
          url: req.url,
          headers: req.headers,
          body: Buffer.concat(chunks).toString()
        };

        res.statusCode = downstreamResponse.statusCode;
        if (downstreamResponse.headers) {
          Object.entries(downstreamResponse.headers).forEach(([key, value]) => {
            res.setHeader(key, value);
          });
        }

        const responseBody = downstreamResponse.body ?? {};
        if (typeof responseBody === 'string') {
          res.end(responseBody);
        } else {
          res.setHeader('content-type', 'application/json');
          res.end(JSON.stringify(responseBody));
        }
      });
    });

    await new Promise((resolve) => downstreamServer.listen(0, resolve));
    const { port } = downstreamServer.address();
    overrideBaseUrl = `http://127.0.0.1:${port}`;
    originalBaseUrl = nconf.get('microservices:dna_papp_service');
    nconf.set('microservices:dna_papp_service', overrideBaseUrl);
  });

  after(async () => {
    if (downstreamServer) {
      await new Promise((resolve) => downstreamServer.close(resolve));
    }
    if (originalBaseUrl !== undefined) {
      nconf.set('microservices:dna_papp_service', originalBaseUrl);
    } else {
      nconf.clear('microservices:dna_papp_service');
    }
  });

  beforeEach(() => {
    lastDownstreamRequest = null;
    downstreamResponse = {
      statusCode: 200,
      body: {
        oid: 'ID901983',
        po_group_num: 'QA',
        is_chat_enabled: true,
        is_schedule_requests_enabled: true,
        is_document_upload_enabled: true
      }
    };
  });

  afterEach(() => {
    sinon.restore();
    if (overrideBaseUrl) {
      nconf.set('microservices:dna_papp_service', overrideBaseUrl);
    }
  });

  describe('GET /api/v1/participant/:participantId/pca-settings', () => {
    it('forwards PCA settings request to the DNA participant app service', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: {
          oid: 'ID901983',
          po_group_num: 'QA',
          is_chat_enabled: true,
          is_schedule_requests_enabled: false,
          is_document_upload_enabled: true
        }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('GET');
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-settings');
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
    });

    it('forwards PCA settings request with query parameters', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: {
          oid: 'ID555111',
          po_group_num: 'PROD',
          is_chat_enabled: false,
          is_schedule_requests_enabled: true,
          is_document_upload_enabled: false
        }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID555111/pca-settings')
        .query({ includeDefaults: 'true' })
        .set(authHeader)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('GET');
      expect(lastDownstreamRequest.url).to.equal(
        '/api/v1/participant/ID555111/pca-settings?includeDefaults=true'
      );
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
    });

    it('propagates downstream 404 error responses', async () => {
      downstreamResponse = {
        statusCode: 404,
        body: { error: 'Participant not found' }
      };

      const res = await request(app)
        .get('/api/v1/participant/UNKNOWN123/pca-settings')
        .set(authHeader)
        .expect(404);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/UNKNOWN123/pca-settings');
    });

    it('propagates downstream 403 error responses', async () => {
      downstreamResponse = {
        statusCode: 403,
        body: { error: 'Access denied' }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(403);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-settings');
    });

    it('propagates downstream 500 error responses', async () => {
      downstreamResponse = {
        statusCode: 500,
        body: { error: 'Internal server error' }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(500);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-settings');
    });

    it('returns 500 when the DNA service base URL is not configured', async () => {
      const originalGet = nconf.get.bind(nconf);
      sinon.stub(nconf, 'get').callsFake((key) => {
        if (key === 'microservices:dna_papp_service') {
          return undefined;
        }
        return originalGet(key);
      });

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(500);

      expect(res.body).to.deep.equal({ error: 'PCA settings retrieval failed.' });
      expect(lastDownstreamRequest).to.be.null;
    });

    it('handles downstream connection errors gracefully', async () => {
      nconf.set('microservices:dna_papp_service', 'http://nonexistent-host:9999');

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(502);

      expect(res.body).to.have.property('error');
      expect(lastDownstreamRequest).to.be.null;
    });

    it('preserves request context and tracing headers', async () => {
      const customTraceId = 'test-trace-12345';

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .set('x-request-id', customTraceId)
        .expect(200);

      expect(lastDownstreamRequest).to.not.be.null;
      expect(res.headers['x-request-id']).to.equal(customTraceId);
    });

    it('handles different participantId formats', async () => {
      const participantIds = ['ID123', 'participant-456', 'abc123xyz', '999'];

      for (const participantId of participantIds) {
        downstreamResponse = {
          statusCode: 200,
          body: {
            oid: participantId,
            po_group_num: 'TEST',
            is_chat_enabled: true,
            is_schedule_requests_enabled: true,
            is_document_upload_enabled: true
          }
        };

        const res = await request(app)
          .get(`/api/v1/participant/${participantId}/pca-settings`)
          .set(authHeader)
          .expect(200);

        expect(res.body.oid).to.equal(participantId);
        expect(lastDownstreamRequest.url).to.equal(
          `/api/v1/participant/${participantId}/pca-settings`
        );
      }
    });

    it('handles empty response body from downstream', async () => {
      downstreamResponse = {
        statusCode: 204,
        body: ''
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(204);

      expect(res.text).to.equal('');
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('forwards content-type header from downstream response', async () => {
      downstreamResponse = {
        statusCode: 200,
        headers: {
          'content-type': 'application/json; charset=utf-8'
        },
        body: {
          oid: 'ID901983',
          po_group_num: 'QA',
          is_chat_enabled: true,
          is_schedule_requests_enabled: true,
          is_document_upload_enabled: true
        }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .expect(200);

      expect(res.headers['content-type']).to.include('application/json');
      expect(lastDownstreamRequest).to.not.be.null;
    });
  });

  describe('POST /api/v1/participant/:participantId/pca-usage', () => {
    const validUsagePayload = {
      timestamp: '2025-12-16T10:30:00Z',
      app: {
        version: '1.2.3',
        settings: {
          is_chat_enabled: true,
          is_schedule_requests_enabled: true,
          is_document_upload_enabled: false
        }
      },
      device: {
        os: 'iOS',
        os_version: '17.2',
        model: 'iPhone 14',
        device_id: 'abc-123-def'
      },
      session_id: 'session-xyz-789'
    };

    it('forwards PCA usage recording to the DNA participant app service', async () => {
      downstreamResponse = {
        statusCode: 201,
        body: {
          message: 'PCA usage recorded successfully'
        }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(201);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('POST');
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-usage');
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
      expect(lastDownstreamRequest.headers['content-type']).to.include('application/json');

      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(validUsagePayload);
    });

    it('forwards PCA usage with different participantId formats', async () => {
      downstreamResponse = {
        statusCode: 201,
        body: { message: 'PCA usage recorded successfully' }
      };

      const participantIds = ['participant-123', 'ID999', 'abc-xyz-789'];

      for (const participantId of participantIds) {
        const res = await request(app)
          .post(`/api/v1/participant/${participantId}/pca-usage`)
          .set(authHeader)
          .send(validUsagePayload)
          .expect(201);

        expect(res.body).to.deep.equal(downstreamResponse.body);
        expect(lastDownstreamRequest.url).to.equal(`/api/v1/participant/${participantId}/pca-usage`);
      }
    });

    it('propagates downstream 400 validation error responses', async () => {
      downstreamResponse = {
        statusCode: 400,
        body: {
          error: 'Invalid request body',
          details: 'timestamp is required'
        }
      };

      const invalidPayload = { ...validUsagePayload };
      delete invalidPayload.timestamp;

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(invalidPayload)
        .expect(400);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('propagates downstream 403 error responses', async () => {
      downstreamResponse = {
        statusCode: 403,
        body: { error: 'Unauthorized access' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(403);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('propagates downstream 404 error responses', async () => {
      downstreamResponse = {
        statusCode: 404,
        body: { error: 'Participant not found' }
      };

      const res = await request(app)
        .post('/api/v1/participant/UNKNOWN999/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(404);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/UNKNOWN999/pca-usage');
    });

    it('propagates downstream 500 error responses', async () => {
      downstreamResponse = {
        statusCode: 500,
        body: { error: 'Database connection failed' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(500);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('returns 500 when the DNA service base URL is not configured', async () => {
      const originalGet = nconf.get.bind(nconf);
      sinon.stub(nconf, 'get').callsFake((key) => {
        if (key === 'microservices:dna_papp_service') {
          return undefined;
        }
        return originalGet(key);
      });

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(500);

      expect(res.body).to.deep.equal({ error: 'PCA usage recording failed.' });
      expect(lastDownstreamRequest).to.be.null;
    });

    it('handles downstream connection errors gracefully', async () => {
      nconf.set('microservices:dna_papp_service', 'http://nonexistent-host:9999');

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(502);

      expect(res.body).to.have.property('error');
      expect(lastDownstreamRequest).to.be.null;
    });

    it('preserves request context and tracing headers', async () => {
      const customTraceId = 'test-usage-trace-456';
      downstreamResponse = {
        statusCode: 201,
        body: { message: 'PCA usage recorded successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .set('x-request-id', customTraceId)
        .send(validUsagePayload)
        .expect(201);

      expect(lastDownstreamRequest).to.not.be.null;
      expect(res.headers['x-request-id']).to.equal(customTraceId);
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(customTraceId);
    });

    it('forwards complex nested usage data correctly', async () => {
      const complexPayload = {
        timestamp: '2025-12-16T15:45:30Z',
        app: {
          version: '2.0.1-beta',
          settings: {
            is_chat_enabled: false,
            is_schedule_requests_enabled: true,
            is_document_upload_enabled: true
          }
        },
        device: {
          os: 'Android',
          os_version: '14.0',
          model: 'Samsung Galaxy S23',
          device_id: 'device-samsung-123',
          screen_resolution: '1080x2400',
          language: 'en-US'
        },
        session_id: 'complex-session-abc-123',
        metadata: {
          network_type: '5G',
          battery_level: 85
        }
      };

      downstreamResponse = {
        statusCode: 201,
        body: { message: 'PCA usage recorded successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/participant-complex/pca-usage')
        .set(authHeader)
        .send(complexPayload)
        .expect(201);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;

      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(complexPayload);
    });

    it('handles downstream response with custom content-type header', async () => {
      downstreamResponse = {
        statusCode: 201,
        headers: {
          'content-type': 'application/json; charset=utf-8'
        },
        body: { message: 'PCA usage recorded successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send(validUsagePayload)
        .expect(201);

      expect(res.headers['content-type']).to.include('application/json');
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('handles empty request body by forwarding as-is', async () => {
      downstreamResponse = {
        statusCode: 400,
        body: { error: 'Request body is required' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-usage')
        .set(authHeader)
        .send({})
        .expect(400);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.body).to.equal('{}');
    });
  });

  describe('POST /api/v1/participant/:participantId/policy', () => {
    const validPolicyPayload = {
      policy_id: 'policy-001',
      acknowledged_at: '2026-03-04T10:00:00Z',
      acknowledged_by: 'ID901983'
    };

    it('forwards policy submission to the DNA participant app service', async () => {
      downstreamResponse = {
        statusCode: 201,
        body: { message: 'Policy submission recorded successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(201);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('POST');
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/policy');
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
      expect(lastDownstreamRequest.headers['content-type']).to.include('application/json');

      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(validPolicyPayload);
    });

    it('forwards policy submission with different participantId formats', async () => {
      downstreamResponse = {
        statusCode: 201,
        body: { message: 'Policy submission recorded successfully' }
      };

      const participantIds = ['participant-123', 'ID999', 'abc-xyz-789'];

      for (const participantId of participantIds) {
        const res = await request(app)
          .post(`/api/v1/participant/${participantId}/policy`)
          .set(authHeader)
          .send(validPolicyPayload)
          .expect(201);

        expect(res.body).to.deep.equal(downstreamResponse.body);
        expect(lastDownstreamRequest.url).to.equal(`/api/v1/participant/${participantId}/policy`);
      }
    });

    it('propagates downstream 400 validation error responses', async () => {
      downstreamResponse = {
        statusCode: 400,
        body: { error: 'Invalid request body', details: 'policy_id is required' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send({})
        .expect(400);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('propagates downstream 403 error responses', async () => {
      downstreamResponse = {
        statusCode: 403,
        body: { error: 'Unauthorized access' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(403);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('propagates downstream 404 error responses', async () => {
      downstreamResponse = {
        statusCode: 404,
        body: { error: 'Participant not found' }
      };

      const res = await request(app)
        .post('/api/v1/participant/UNKNOWN999/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(404);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/UNKNOWN999/policy');
    });

    it('propagates downstream 500 error responses', async () => {
      downstreamResponse = {
        statusCode: 500,
        body: { error: 'Database connection failed' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(500);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('returns 500 when the DNA service base URL is not configured', async () => {
      const originalGet = nconf.get.bind(nconf);
      sinon.stub(nconf, 'get').callsFake((key) => {
        if (key === 'microservices:dna_papp_service') {
          return undefined;
        }
        return originalGet(key);
      });

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(500);

      expect(res.body).to.deep.equal({ error: 'Policy submission failed.' });
      expect(lastDownstreamRequest).to.be.null;
    });

    it('handles downstream connection errors gracefully', async () => {
      nconf.set('microservices:dna_papp_service', 'http://nonexistent-host:9999');

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(validPolicyPayload)
        .expect(502);

      expect(res.body).to.have.property('error');
      expect(lastDownstreamRequest).to.be.null;
    });

    it('preserves request context and tracing headers', async () => {
      const customTraceId = 'test-policy-trace-789';
      downstreamResponse = {
        statusCode: 201,
        body: { message: 'Policy submission recorded successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .set('x-request-id', customTraceId)
        .send(validPolicyPayload)
        .expect(201);

      expect(lastDownstreamRequest).to.not.be.null;
      expect(res.headers['x-request-id']).to.equal(customTraceId);
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(customTraceId);
    });

    it('forwards the request body verbatim', async () => {
      const arbitraryPayload = {
        custom_field: 'value',
        nested: { a: 1, b: [2, 3] }
      };

      downstreamResponse = {
        statusCode: 200,
        body: { status: 'ok' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/policy')
        .set(authHeader)
        .send(arbitraryPayload)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(arbitraryPayload);
    });
  });

  describe('GET /api/v1/participant/:participantId/pca-contacts', () => {
    it('forwards PCA contacts request to the DNA participant app service', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: {
          contacts: [
            { name: 'Officer Smith', phone: '555-1234', role: 'supervisor' }
          ]
        }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('GET');
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-contacts');
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
    });

    it('forwards PCA contacts request with query parameters', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: { contacts: [] }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID555111/pca-contacts')
        .query({ type: 'emergency' })
        .set(authHeader)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.url).to.equal(
        '/api/v1/participant/ID555111/pca-contacts?type=emergency'
      );
    });

    it('propagates downstream 404 error responses', async () => {
      downstreamResponse = {
        statusCode: 404,
        body: { error: 'Participant not found' }
      };

      const res = await request(app)
        .get('/api/v1/participant/UNKNOWN123/pca-contacts')
        .set(authHeader)
        .expect(404);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/UNKNOWN123/pca-contacts');
    });

    it('propagates downstream 403 error responses', async () => {
      downstreamResponse = {
        statusCode: 403,
        body: { error: 'Access denied' }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .expect(403);

      expect(res.body).to.deep.equal(downstreamResponse.body);
    });

    it('propagates downstream 500 error responses', async () => {
      downstreamResponse = {
        statusCode: 500,
        body: { error: 'Internal server error' }
      };

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .expect(500);

      expect(res.body).to.deep.equal(downstreamResponse.body);
    });

    it('returns 500 when the DNA service base URL is not configured', async () => {
      const originalGet = nconf.get.bind(nconf);
      sinon.stub(nconf, 'get').callsFake((key) => {
        if (key === 'microservices:dna_papp_service') {
          return undefined;
        }
        return originalGet(key);
      });

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .expect(500);

      expect(res.body).to.deep.equal({ error: 'PCA contacts retrieval failed.' });
      expect(lastDownstreamRequest).to.be.null;
    });

    it('handles downstream connection errors gracefully', async () => {
      nconf.set('microservices:dna_papp_service', 'http://nonexistent-host:9999');

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .expect(502);

      expect(res.body).to.have.property('error');
      expect(lastDownstreamRequest).to.be.null;
    });

    it('preserves request context and tracing headers', async () => {
      const customTraceId = 'test-contacts-trace-321';

      const res = await request(app)
        .get('/api/v1/participant/ID901983/pca-contacts')
        .set(authHeader)
        .set('x-request-id', customTraceId)
        .expect(200);

      expect(lastDownstreamRequest).to.not.be.null;
      expect(res.headers['x-request-id']).to.equal(customTraceId);
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(customTraceId);
    });

    it('handles different participantId formats', async () => {
      const participantIds = ['ID123', 'participant-456', 'abc123xyz', '999'];

      for (const participantId of participantIds) {
        downstreamResponse = {
          statusCode: 200,
          body: { contacts: [] }
        };

        const res = await request(app)
          .get(`/api/v1/participant/${participantId}/pca-contacts`)
          .set(authHeader)
          .expect(200);

        expect(lastDownstreamRequest.url).to.equal(
          `/api/v1/participant/${participantId}/pca-contacts`
        );
      }
    });
  });

  describe('POST /api/v1/participant/:participantId/pca-settings', () => {
    const validSettingsPayload = {
      pushnotifications: {
        'schedule-request': false
      }
    };

    it('forwards PCA settings update to the DNA participant app service', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: { message: 'PCA settings updated successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(200);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
      expect(lastDownstreamRequest.method).to.equal('POST');
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/ID901983/pca-settings');
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(res.headers['x-request-id']);
      expect(lastDownstreamRequest.headers['content-type']).to.include('application/json');

      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(validSettingsPayload);
    });

    it('forwards request body verbatim to downstream', async () => {
      const extendedPayload = {
        pushnotifications: {
          'schedule-request': true,
          'document-upload': false
        },
        other_setting: 'value'
      };

      downstreamResponse = {
        statusCode: 200,
        body: { message: 'PCA settings updated successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(extendedPayload)
        .expect(200);

      const receivedBody = JSON.parse(lastDownstreamRequest.body);
      expect(receivedBody).to.deep.equal(extendedPayload);
    });

    it('forwards PCA settings update with different participantId formats', async () => {
      downstreamResponse = {
        statusCode: 200,
        body: { message: 'PCA settings updated successfully' }
      };

      const participantIds = ['participant-123', 'ID999', 'abc-xyz-789'];

      for (const participantId of participantIds) {
        const res = await request(app)
          .post(`/api/v1/participant/${participantId}/pca-settings`)
          .set(authHeader)
          .send(validSettingsPayload)
          .expect(200);

        expect(lastDownstreamRequest.url).to.equal(
          `/api/v1/participant/${participantId}/pca-settings`
        );
      }
    });

    it('propagates downstream 400 validation error responses', async () => {
      downstreamResponse = {
        statusCode: 400,
        body: { error: 'Invalid request body' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send({})
        .expect(400);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest).to.not.be.null;
    });

    it('propagates downstream 403 error responses', async () => {
      downstreamResponse = {
        statusCode: 403,
        body: { error: 'Unauthorized access' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(403);

      expect(res.body).to.deep.equal(downstreamResponse.body);
    });

    it('propagates downstream 404 error responses', async () => {
      downstreamResponse = {
        statusCode: 404,
        body: { error: 'Participant not found' }
      };

      const res = await request(app)
        .post('/api/v1/participant/UNKNOWN999/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(404);

      expect(res.body).to.deep.equal(downstreamResponse.body);
      expect(lastDownstreamRequest.url).to.equal('/api/v1/participant/UNKNOWN999/pca-settings');
    });

    it('propagates downstream 500 error responses', async () => {
      downstreamResponse = {
        statusCode: 500,
        body: { error: 'Internal server error' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(500);

      expect(res.body).to.deep.equal(downstreamResponse.body);
    });

    it('returns 500 when the DNA service base URL is not configured', async () => {
      const originalGet = nconf.get.bind(nconf);
      sinon.stub(nconf, 'get').callsFake((key) => {
        if (key === 'microservices:dna_papp_service') {
          return undefined;
        }
        return originalGet(key);
      });

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(500);

      expect(res.body).to.deep.equal({ error: 'PCA settings update failed.' });
      expect(lastDownstreamRequest).to.be.null;
    });

    it('handles downstream connection errors gracefully', async () => {
      nconf.set('microservices:dna_papp_service', 'http://nonexistent-host:9999');

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .send(validSettingsPayload)
        .expect(502);

      expect(res.body).to.have.property('error');
      expect(lastDownstreamRequest).to.be.null;
    });

    it('preserves request context and tracing headers', async () => {
      const customTraceId = 'test-pca-settings-update-trace-001';
      downstreamResponse = {
        statusCode: 200,
        body: { message: 'PCA settings updated successfully' }
      };

      const res = await request(app)
        .post('/api/v1/participant/ID901983/pca-settings')
        .set(authHeader)
        .set('x-request-id', customTraceId)
        .send(validSettingsPayload)
        .expect(200);

      expect(lastDownstreamRequest).to.not.be.null;
      expect(res.headers['x-request-id']).to.equal(customTraceId);
      expect(lastDownstreamRequest.headers['x-request-id']).to.equal(customTraceId);
    });
  });
});
