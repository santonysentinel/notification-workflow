import jwt from 'jsonwebtoken';

const jwtKey = process.env.jwtKey;

const extractJWTHeader = (req) => {
  let token = '';
  if (req.headers && req.headers.authorization) {
    const splitauth = req.headers.authorization.split(' ');
    if (splitauth.length === 2 && splitauth[0] === 'Bearer') {
      token = splitauth[1];
    }
  }
  return token;
};

const verifyAndExtractPermissionsFromJWT = (req) => {
  return new Promise((resolve, reject) => {
    jwt.verify(req.token, jwtKey, (err, decoded) => {
      if (err) {
        reject(new Error('401'));
      } else {
        const permissions = decoded?.permissions;
        if (permissions) {
          resolve(permissions);
        } else {
          reject(new Error('403'));
        }
      }
    });
  });
};

const extractUserDataFromJWT = (req) => {
  return new Promise((resolve, reject) => {
    jwt.verify(req.token, jwtKey, (err, decoded) => {
      if (err) {
        reject(new Error('401'));
      } else {
        if (decoded) {
          resolve(decoded);
        } else {
          reject(new Error('403'));
        }
      }
    });
  });
};

export { extractJWTHeader, verifyAndExtractPermissionsFromJWT, extractUserDataFromJWT };
