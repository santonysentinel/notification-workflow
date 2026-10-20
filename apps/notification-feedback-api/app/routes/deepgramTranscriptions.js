import express from 'express';
import nconf from 'nconf';
import requireDeepgramCallbackJwt from '../middleware/requireDeepgramCallbackJwt.js';
import { createDeepgramCallbackController } from '../controllers/deepgramCallbackController.js';
import { sendError } from '../../configure/errors.js';

export function readDeepgramCallbackConfig() {
  return {
    issuer: process.env.DEEPGRAM_CALLBACK_JWT_ISSUER ?? nconf.get('deepgram:callback:issuer'),
    audience: process.env.DEEPGRAM_CALLBACK_JWT_AUDIENCE ?? nconf.get('deepgram:callback:audience'),
    permissionId:
      process.env.DEEPGRAM_CALLBACK_PERMISSION_ID ?? nconf.get('deepgram:callback:permissionId'),
    identityBaseUrl:
      process.env.DEEPGRAM_IDENTITY_BASE_URL ?? nconf.get('deepgram:callback:identityBaseUrl'),
    jwtJwksPath:
      process.env.DEEPGRAM_IDENTITY_JWKS_PATH ??
      nconf.get('deepgram:callback:jwtJwksPath') ??
      '/.well-known/jwks.json',
    allowInsecureIdentityHttp:
      process.env.DEEPGRAM_IDENTITY_ALLOW_HTTP === undefined
        ? nconf.get('deepgram:callback:allowInsecureIdentityHttp') === true
        : process.env.DEEPGRAM_IDENTITY_ALLOW_HTTP === 'true',
    bypassJwtVerification:
      process.env.DEEPGRAM_CALLBACK_BYPASS_JWT_VERIFICATION === undefined
        ? nconf.get('deepgram:callback:bypassJwtVerification') === true
        : process.env.DEEPGRAM_CALLBACK_BYPASS_JWT_VERIFICATION === 'true',
    algorithm:
      process.env.DEEPGRAM_CALLBACK_JWT_ALGORITHM ??
      nconf.get('deepgram:callback:algorithm') ??
      'RS256',
    publicKey:
      process.env.DEEPGRAM_CALLBACK_JWT_PUBLIC_KEY ?? nconf.get('deepgram:callback:publicKey'),
    jwksUrl: process.env.DEEPGRAM_CALLBACK_JWKS_URL ?? nconf.get('deepgram:callback:jwksUrl'),
    database: process.env.AUTO_CALL_DATABASE ?? nconf.get('twilio:database') ?? 'AutoCallDB'
  };
}

export function createDeepgramTranscriptionsRouter({
  repository,
  getConfig = readDeepgramCallbackConfig
} = {}) {
  const router = express.Router();
  router.post(
    '/completed',
    (req, res, next) => {
      res.set('Cache-Control', 'no-store');
      if (!req.is('application/json')) return sendError(res, 415, 'A JSON request is required');
      if (Object.keys(req.query).length)
        return sendError(res, 400, 'Callback query parameters are not supported');
      return next();
    },
    requireDeepgramCallbackJwt({ getConfig }),
    express.json({ limit: '8mb', strict: true }),
    createDeepgramCallbackController(repository)
  );
  router.use((err, req, res, next) => {
    if (res.headersSent) return next(err);
    const status = err?.status === 413 ? 413 : err?.type === 'entity.parse.failed' ? 400 : 500;
    return sendError(
      res,
      status,
      status === 413 ? 'Callback payload is too large' : 'Callback could not be processed',
      { path: req.path }
    );
  });
  return router;
}

export default createDeepgramTranscriptionsRouter();
