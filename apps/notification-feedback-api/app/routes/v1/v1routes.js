import express from 'express';
import autoCallApi from './autoCall.js';

const api = express.Router();

console.log('[ROUTE] - Configuring auto call routes ...');


api.use(autoCallApi);

// Must come after your API routes
// This middleware handles file size errors for document uploads
// Multer includes the per-route limit in `err.limit`
api.use((err, req, res, next) => {
  if (err?.code === 'LIMIT_FILE_SIZE') {
    const limitBytes = err.limit || 0;
    const limitMb = limitBytes ? Math.round(limitBytes / (1024 * 1024)) : null;
    const message = limitMb
      ? `File too large. Max allowed is ${limitMb}MB.`
      : 'File too large. Uploaded file exceeds configured limit.';

    return res.status(413).json({
      error: message,
      limitBytes: limitBytes || undefined
    });
  }

  return next(err);
});

export default api;
