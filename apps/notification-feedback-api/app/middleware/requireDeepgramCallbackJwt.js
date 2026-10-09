import { createHash } from 'node:crypto';
import { createRemoteJWKSet, importSPKI, jwtVerify } from 'jose';
import { sendError } from '../../configure/errors.js';

export const correlationPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let cachedTrust;

export async function verifyDeepgramCallbackJwt(token, config) {
  const algorithm = config.algorithm ?? 'RS256';
  let jwksUrl;
  if (!config.publicKey) {
    try {
      jwksUrl = new URL(config.jwksUrl);
    } catch {
      throw Object.assign(new Error('Configuration unavailable'), { status: 503 });
    }
    if (
      jwksUrl.protocol !== 'https:' ||
      jwksUrl.username ||
      jwksUrl.password ||
      jwksUrl.search ||
      jwksUrl.hash
    ) {
      throw Object.assign(new Error('Configuration unavailable'), { status: 503 });
    }
  }
  if (
    !['RS256', 'ES256'].includes(algorithm) ||
    typeof config.issuer !== 'string' ||
    !config.issuer.trim() ||
    typeof config.audience !== 'string' ||
    !config.audience.trim() ||
    typeof config.scope !== 'string' ||
    !config.scope.trim()
  ) {
    throw Object.assign(new Error('Configuration unavailable'), { status: 503 });
  }
  const identity = JSON.stringify([algorithm, config.publicKey ?? jwksUrl.href]);
  if (cachedTrust?.identity !== identity) {
    try {
      const key = config.publicKey
        ? await importSPKI(config.publicKey, algorithm)
        : createRemoteJWKSet(jwksUrl, {
            timeoutDuration: 5000,
            cooldownDuration: 30000,
            cacheMaxAge: 600000
          });
      cachedTrust = { identity, key };
    } catch {
      throw Object.assign(new Error('Configuration unavailable'), { status: 503 });
    }
  }
  try {
    const { payload } = await jwtVerify(token, cachedTrust.key, {
      algorithms: [algorithm],
      issuer: config.issuer,
      audience: config.audience,
      requiredClaims: ['exp', 'jti', 'scope', 'submission_id', 'recording_id'],
      clockTolerance: 5
    });
    if (
      typeof payload.jti !== 'string' ||
      !payload.jti.trim() ||
      typeof payload.scope !== 'string' ||
      !payload.scope.split(/\s+/).includes(config.scope) ||
      typeof payload.submission_id !== 'string' ||
      !correlationPattern.test(payload.submission_id) ||
      !Number.isInteger(payload.recording_id) ||
      payload.recording_id <= 0 ||
      payload.recording_id > 2147483647
    ) {
      throw new Error('Invalid claims');
    }
    return payload;
  } catch (err) {
    const unavailable =
      err?.code === 'ERR_JWKS_TIMEOUT' ||
      err instanceof TypeError ||
      (jwksUrl && ['ERR_JOSE_GENERIC', 'ERR_JWKS_INVALID', 'ERR_JWK_INVALID'].includes(err?.code));
    throw Object.assign(new Error('Callback verification failed'), {
      status: unavailable ? 503 : 401
    });
  }
}

export default function requireDeepgramCallbackJwt({
  getConfig,
  verifyJwt = verifyDeepgramCallbackJwt
}) {
  return async (req, res, next) => {
    res.set('Cache-Control', 'no-store');
    const context = { traceId: req.context?.traceId, path: req.path };
    const header = req.get('Authorization');
    const match = /^Basic ([A-Za-z0-9+/]+={0,2})$/i.exec(header ?? '');
    const duplicates = req.rawHeaders.filter(
      (value, index) => index % 2 === 0 && value.toLowerCase() === 'authorization'
    ).length;
    if (!match || duplicates !== 1 || match[1].length > 24000)
      return sendError(res, 401, 'Invalid callback authentication', context);
    const bytes = Buffer.from(match[1], 'base64');
    const credentials = bytes.toString('utf8');
    const separator = credentials.indexOf(':');
    if (bytes.toString('base64') !== match[1] || separator < 0)
      return sendError(res, 401, 'Invalid callback authentication', context);
    const correlationId = credentials.slice(0, separator).toLowerCase();
    const token = credentials.slice(separator + 1);
    if (
      !correlationPattern.test(correlationId) ||
      !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)
    ) {
      return sendError(res, 401, 'Invalid callback authentication', context);
    }
    try {
      const config = getConfig();
      if (typeof config.database !== 'string' || !config.database.trim())
        throw Object.assign(new Error('Configuration unavailable'), { status: 503 });
      const claims = await verifyJwt(token, config);
      if (claims.submission_id.toLowerCase() !== correlationId)
        return sendError(res, 401, 'Invalid callback authentication', context);
      req.deepgramCallback = {
        correlationId,
        recordingId: claims.recording_id,
        tokenHash: createHash('sha256').update(token, 'utf8').digest(),
        database: config.database
      };
      return next();
    } catch (err) {
      return sendError(
        res,
        err?.status === 503 ? 503 : 401,
        err?.status === 503
          ? 'Callback verification is unavailable'
          : 'Invalid callback authentication',
        context
      );
    }
  };
}
