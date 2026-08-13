import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import eslintConfigPrettier from 'eslint-config-prettier';

export default tseslint.config(
  js.configs.recommended,
  ...tseslint.configs.recommended,
  eslintConfigPrettier,
  {
    ignores: ['cdk.out/**', 'node_modules/**', '*.js'],
  },
  {
    // aws-cdk-lib/assertions' Template.findResources()/findOutputs() return
    // loosely-typed CFN-JSON records — casting to `any` when reading
    // .Properties off them is the idiomatic pattern for CDK template
    // assertions (see any CDK repo's own test suite), not a real type-safety
    // gap. Scoped to test files only; application code still gets the full
    // no-explicit-any rule.
    files: ['test/**/*.ts'],
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
);
