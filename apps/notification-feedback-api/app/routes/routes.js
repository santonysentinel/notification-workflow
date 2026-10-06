import apiVersion1 from '../routes/v1/v1routes.js';

export default function (app) {
  console.log('Inside routes');
  console.log('[ROUTE] - For /api/v1 configured');
  app.use('/api/v1', apiVersion1);
}
