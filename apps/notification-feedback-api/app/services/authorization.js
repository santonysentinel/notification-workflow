
function AuthenticateandAuthorize(req, res, route) {
  return new Promise((resolve, reject) => {
    const result = {
      message: '',
      sessionvalid: false,
      status: 200
    };

    resolve(result);

    // Uncomment if you want full verification logic enabled
    /*
    const token = extractJWTHeader(req);
    if (!token) {
      result.message = 'Unauthorized';
      result.status = 401;
      reject(Errors.FOUR_ZERO_ONE);
    }

    req.token = token;
    resolve(result);

    
    verifyAndExtractPermissionsFromJWT(req)
      .then((permissions) => {
        result.sessionvalid = true;

        const isAllowed = IsRouteAllowed(permissions, route || 0);
        if (!isAllowed) {
          console.log(req.uuid, `Unauthorized API access to ${route}`);
          reject({ ...Errors.FOUR_ZERO_ONE, message: 'Unauthorized API access' });
        } else {
          console.log(req.uuid, `Authorized API access to ${route}`);
          resolve(result);
        }
      })
      .catch((error) => {
        if (error.message === '401') {
          reject(Errors.FOUR_ZERO_ONE);
        } else if (error.message === '403') {
          reject(Errors.FOUR_ZERO_THREE);
        } else {
          reject({
            ...Errors.FIVE_ZERO_ZERO,
            status_code: 500,
            message: 'Internal Error'
          });
        }
      });
    */
  });
}

export default AuthenticateandAuthorize;
