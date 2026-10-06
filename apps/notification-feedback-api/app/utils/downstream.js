import nconf from 'nconf';

export const relayDownstreamResponse = (res, response) => {
  if (response.headers?.['content-type']) {
    res.set('content-type', response.headers['content-type']);
  }

  res.status(response.status);
  if (response.data === undefined) {
    res.end();
  } else {
    res.send(response.data);
  }
};

export const resolveServiceBase = (serviceKey, friendlyName) => {
  const baseUrl = nconf.get(`microservices:${serviceKey}`);
  if (!baseUrl) {
    const name = friendlyName || serviceKey;
    throw new Error(`${name} base URL is not configured`);
  }
  return baseUrl;
};
