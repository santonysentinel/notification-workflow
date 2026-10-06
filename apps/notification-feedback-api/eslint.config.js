import js from '@eslint/js';
import globals from 'globals';
import eslintConfigPrettier from 'eslint-config-prettier';
import eslintPluginPrettier from 'eslint-plugin-prettier';

const prettierRules = eslintConfigPrettier?.rules ?? {};

export default [
  {
    ignores: ['**/node_modules', 'log/**', 'uploads/**', 'pnpm-lock.yaml']
  },
  js.configs.recommended,
  {
    files: ['**/*.js'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: {
        ...globals.node,
        ...globals.es2022
      }
    }
  },
  {
    plugins: {
      prettier: eslintPluginPrettier
    },
    rules: {
      ...prettierRules,
      'prettier/prettier': 'error'
    }
  }
];
