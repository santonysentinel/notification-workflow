import { createHash } from 'node:crypto';
import { createRemoteJWKSet, customFetch, importSPKI, jwtVerify } from 'jose';
import { sendError } from '../../configure/errors.js';

export const correlationPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let cachedTrust;
const unavailableReasons = new Set([
  'JWKS_URL_INVALID',
  'JWKS_URL_UNSAFE',
  'IDENTITY_HTTP_BASE_INVALID',
  'IDENTITY_HTTP_NOT_ALLOWED',
  'CALLBACK_TRUST_INVALID',
  'CALLBACK_KEY_UNAVAILABLE',
  'CALLBACK_DATABASE_MISSING',
  'JWKS_VERIFICATION_UNAVAILABLE'
]);

async function fetchKeys(url, options) {
  const timeout = AbortSignal.timeout(5000);
  const signal = options.signal ? AbortSignal.any([options.signal, timeout]) : timeout;
  const response = await fetch(url, { ...options, signal, redirect: 'error' });
  if (response.status !== 200) return response;
  if (Number(response.headers.get('content-length')) > 65536 || !response.body) {
    await response.body?.cancel();
    throw new TypeError('Key response unavailable');
  }
  const reader = response.body.getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      signal.throwIfAborted();
      length += value.length;
      if (length > 65536) throw new TypeError('Key response unavailable');
      chunks.push(Buffer.from(value));
    }
    return new Response(Buffer.concat(chunks), {
      status: response.status,
      headers: response.headers
    });
  } finally {
    await reader.cancel();
    reader.releaseLock();
  }
}

export async function verifyDeepgramCallbackJwt(token, config) {
  const algorithm = config.algorithm ?? 'RS256';
  let jwksUrl;
  if (!config.publicKey) {
    try {
      jwksUrl = config.jwksUrl
        ? new URL(config.jwksUrl)
        : new URL(config.jwtJwksPath ?? '/.well-known/jwks.json', config.identityBaseUrl);
    } catch {
      throw Object.assign(new Error('Configuration unavailable'), {
        status: 503,
        reason: 'JWKS_URL_INVALID'
      });
    }
    if (
      !['https:', 'http:'].includes(jwksUrl.protocol) ||
      jwksUrl.username ||
      jwksUrl.password ||
      jwksUrl.search ||
      jwksUrl.hash
    ) {
      throw Object.assign(new Error('Configuration unavailable'), {
        status: 503,
        reason: 'JWKS_URL_UNSAFE'
      });
    }
    if (jwksUrl.protocol === 'http:') {
      let expected;
      try {
        const base = new URL(config.identityBaseUrl);
        if (base.username || base.password || base.search || base.hash) throw new Error();
        expected = new URL(config.jwtJwksPath ?? '/.well-known/jwks.json', base);
        if (expected.origin !== base.origin) throw new Error();
      } catch {
        throw Object.assign(new Error('Configuration unavailable'), {
          status: 503,
          reason: 'IDENTITY_HTTP_BASE_INVALID'
        });
      }
      if (config.allowInsecureIdentityHttp !== true || expected.href !== jwksUrl.href)
        throw Object.assign(new Error('Configuration unavailable'), {
          status: 503,
          reason: 'IDENTITY_HTTP_NOT_ALLOWED'
        });
    }
  }
  if (
    !['RS256', 'ES256'].includes(algorithm) ||
    typeof config.issuer !== 'string' ||
    !config.issuer.trim() ||
    typeof config.audience !== 'string' ||
    !config.audience.trim() ||
    !/^[1-9]\d*$/.test(String(config.permissionId ?? ''))
  ) {
    throw Object.assign(new Error('Configuration unavailable'), {
      status: 503,
      reason: 'CALLBACK_TRUST_INVALID'
    });
  }
  const identity = JSON.stringify([algorithm, config.publicKey ?? jwksUrl.href]);
  if (cachedTrust?.identity !== identity) {
    try {
      const key = config.publicKey
        ? await importSPKI(config.publicKey, algorithm)
        : createRemoteJWKSet(jwksUrl, {
            timeoutDuration: 5000,
            cooldownDuration: 30000,
            cacheMaxAge: 600000,
            [customFetch]: fetchKeys
          });
      cachedTrust = { identity, key };
    } catch {
      throw Object.assign(new Error('Configuration unavailable'), {
        status: 503,
        reason: 'CALLBACK_KEY_UNAVAILABLE'
      });
    }
  }
  try {
    const { payload } = await jwtVerify(token, cachedTrust.key, {
      algorithms: [algorithm],
      issuer: config.issuer,
      audience: config.audience,
      requiredClaims: ['exp', 'permissions'],
      clockTolerance: 5
    });
    if (
      typeof payload.permissions !== 'string' ||
      payload.permissions.length > 16000 ||
      !payload.permissions.split(',').every((value) => /^[1-9]\d*$/.test(value.trim())) ||
      !payload.permissions
        .split(',')
        .map((value) => value.trim())
        .includes(String(config.permissionId))
    ) {
      throw new Error('Invalid claims');
    }
    return payload;
  } catch (err) {
    const unavailable =
      err?.code === 'ERR_JWKS_TIMEOUT' ||
      ['AbortError', 'TimeoutError'].includes(err?.name) ||
      err instanceof TypeError ||
      (jwksUrl && ['ERR_JOSE_GENERIC', 'ERR_JWKS_INVALID', 'ERR_JWK_INVALID'].includes(err?.code));
    throw Object.assign(new Error('Callback verification failed'), {
      status: unavailable ? 503 : 401,
      reason: unavailable ? 'JWKS_VERIFICATION_UNAVAILABLE' : 'JWT_REJECTED'
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
        throw Object.assign(new Error('Configuration unavailable'), {
          status: 503,
          reason: 'CALLBACK_DATABASE_MISSING'
        });
      if (config.bypassJwtVerification === true) {
        req.log?.warn('deepgram.callback.jwt_verification.bypassed', {
          verificationBypassed: true,
          correlationId
        });
      } else {
        await verifyJwt(token, config);
      }
      req.deepgramCallback = {
        correlationId,
        tokenHash: createHash('sha256').update(token, 'utf8').digest(),
        database: config.database
      };
      return next();
    } catch (err) {
      if (err?.status === 503) {
        req.log?.error('deepgram.callback.verification.unavailable', {
          statusCode: 503,
          reason: unavailableReasons.has(err.reason)
            ? err.reason
            : 'CALLBACK_VERIFICATION_UNAVAILABLE'
        });
      }
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
