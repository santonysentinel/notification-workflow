import express from 'express';
import { getParticipantPcaContacts } from '../../../controllers/participantPcaContactsController.js';
import { requireRoute } from '../../../middleware/authzMiddleware.js';
import { AppRoutes } from '../../../services/approutes.js';

const router = express.Router({ mergeParams: true });

router.get(
  '/:participantId/pca-contacts',
  requireRoute(AppRoutes.GET_PARTICIPANT_PCA_CONTACTS),
  getParticipantPcaContacts
);

export default router;
