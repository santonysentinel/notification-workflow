import express from 'express';
import request from 'supertest';
import { describe, it } from 'mocha';
import { expect } from 'chai';
import requireBearerToken from '../app/middleware/requireBearerToken.js';

const buildApp = () => {
  const app = express();

  app.use(
    requireBearerToken({
      skipPaths: ['/public']
    })
  );

  app.get('/secure/data', (_req, res) => {
    res.json({ ok: true });
  });

  app.get('/public/info', (_req, res) => {
    res.json({ public: true });
  });

  return app;
};

describe('requireBearerToken middleware', () => {
  let app;

  before(() => {
    app = buildApp();
  });

  it('rejects requests without a bearer token', async () => {
    const res = await request(app).get('/secure/data').expect(401);
    expect(res.body).to.deep.equal({ error: 'Unauthorized. Bearer token required.' });
  });

  it('allows requests with a bearer token', async () => {
    const res = await request(app)
      .get('/secure/data')
      .set('Authorization', 'Bearer demo-token')
      .expect(200);

    expect(res.body).to.deep.equal({ ok: true });
  });

  it('skips paths configured in options', async () => {
    const res = await request(app).get('/public/info').expect(200);
    expect(res.body).to.deep.equal({ public: true });
  });
});
