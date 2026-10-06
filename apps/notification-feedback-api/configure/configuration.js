import 'dotenv/config';

console.log(process.env.NODE_ENV);
const environment = process.env.NODE_ENV || 'development';
console.log(`Environment is set to ${environment}`);

const DEV_FILENAME = 'config.json';
const PROD_FILENAME = 'config-prod.json';
const QA_FILENAME = 'config-qa.json';

/* Returns a file name for the app to use correct configs based on environment */
const getEnvConfigFileName = () => {
  const lowerCaseEnv = environment.toLowerCase();
  switch (lowerCaseEnv) {
    case 'production':
    case 'prod':
      return PROD_FILENAME;
    case 'qa':
      return QA_FILENAME;
    default:
      return DEV_FILENAME;
  }
};

const configFile = getEnvConfigFileName();
console.log(`Config File: ${configFile}`);

export default configFile;
