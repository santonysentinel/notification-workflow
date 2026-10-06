const extractQueryParams = (req) => {
  try {
    const queryParams = req.query;
    const querystring = Object.entries(queryParams)
      .map(([key, val]) => `${key}=${val}`)
      .join('&');
    if (querystring && querystring.length > 2) {
      return `?${querystring}`;
    }
    return '';
  } catch {
    return '';
  }
};

export { extractQueryParams };
