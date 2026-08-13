import * as fs from 'fs';
import * as path from 'path';

// Lambda asset bundling (lambda.Code.fromAsset) reads each worker's publish
// output from disk at synth time, so a placeholder is created here rather
// than requiring `dotnet publish` to have already run before `npm test`.
export function createWorkerPlaceholders(names: string[]): void {
  for (const name of names) {
    const publishDir = path.join(
      __dirname,
      '..',
      '..',
      '..',
      'apps',
      'workers',
      `SentinelOps.Workers.${name}`,
      'bin',
      'Release',
      'net10.0',
      'publish',
    );
    fs.mkdirSync(publishDir, { recursive: true });
    fs.writeFileSync(
      path.join(publishDir, '.placeholder'),
      'test-only placeholder for cdk asset bundling\n',
    );
  }
}
