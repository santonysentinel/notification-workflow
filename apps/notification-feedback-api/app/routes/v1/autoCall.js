import express from 'express';
import { createAutoCall } from '../../controllers/autoCallController.js';

const router = express.Router({ mergeParams: true });

router.post('/autocall', createAutoCall);

export default router;
