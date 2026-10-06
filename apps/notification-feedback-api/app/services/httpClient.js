import axios from 'axios';

// const API_URL = create config to get url based on environment
const API_URL = process.env.authService;

const httpClient = async (method, url, data, additionalHeaders) => {
  return new Promise((resolve) => {
    const headers = { ...additionalHeaders };
    axios({
      url: `${API_URL}/${url}`,
      method,
      headers,
      data
    }).then(
      (response) => {
        resolve(response);
      },
      (error) => {
        resolve(error.response);
      }
    );
  });
};

export default httpClient;
