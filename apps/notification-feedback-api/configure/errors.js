class ApiError extends Error {
  constructor({
    statusCode = 500,
    message = 'Internal Error',
    errorCode = 0,
    errorField = null,
    details = null,
    cause = null
  } = {}) {
    super(message);
    this.name = 'ApiError';
    this.statusCode = statusCode;
    this.errorCode = errorCode;
    this.errorField = errorField;
    this.details = details;
    this.cause = cause;
  }
  toJSON({ traceId = null, path = null } = {}) {
    return {
      status_code: this.statusCode,
      message: this.message,
      error_code: this.errorCode,
      error_field: this.errorField ?? null,
      trace_id: traceId ?? null,
      path: path ?? null,
      details: this.details ?? null,
      timestamp: new Date().toISOString()
    };
  }
}

const Errors = {
  FOUR_ZERO_ZERO: {
    status_code: 400,
    message: 'Missing required field(s)',
    error_code: 1000,
    error_field: null
  },
  FOUR_ZERO_ONE: {
    status_code: 401,
    message: 'Unauthorized resource access',
    error_code: 1001,
    error_field: null
  },
  FOUR_ZERO_THREE: {
    status_code: 403,
    message: 'Session access token has expired',
    error_code: 1003,
    error_field: null
  },
  FOUR_ZERO_FOUR: {
    status_code: 404,
    message: 'Resource not found',
    error_code: 1004,
    error_field: null
  },
  FIVE_ZERO_ZERO: {
    status_code: 500,
    message: 'Internal Error',
    error_code: 1500,
    error_field: null
  },
  GENERIC: { status_code: 0, message: '', error_code: 0, error_field: null }
};

function makeError(statusCode, message, { errorCode, errorField, details, cause } = {}) {
  return new ApiError({ statusCode, message, errorCode, errorField, details, cause });
}

function sendError(
  res,
  statusCode,
  message,
  { errorCode, errorField, details, cause, traceId, path } = {}
) {
  const err = makeError(statusCode, message, { errorCode, errorField, details, cause });
  res.status(statusCode).json(err.toJSON({ traceId, path }));
}

export { Errors, ApiError, makeError, sendError };
