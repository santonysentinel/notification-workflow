import defaultAuth from '../services/authorization.js';
import { Errors } from '../../configure/errors.js';

let auth = defaultAuth;

export const __setAuthForTests = (mockAuth) => {
  auth = mockAuth;
};

export const __resetAuthForTests = () => {
  auth = defaultAuth;
};

const baseController = async (req, res, route, routeController) => {
  try {
    await auth(req, res, route);
    routeController(req, res);
  } catch (err) {
    // Authentication  fail or
    // Authorization fail
    console.log(req.uuid, JSON.stringify(err) || '');

    if (err.status_code) {
      if (err.status_code === 401) {
        res.statusCode = 401;
        res.send(Errors.FOUR_ZERO_ONE);
      } else if (err.status_code === 403) {
        res.statusCode = 403;
        res.send(Errors.FOUR_ZERO_THREE);
      } else {
        res.statusCode = err.status_code || 500;
        res.send({
          ...Errors.FIVE_ZERO_ZERO,
          status_code: err.status_code || 500
        });
      }
    } else {
      res.status = 500;
      res.send(Errors.FIVE_ZERO_ZERO);
    }
  }
};

export default baseController;
