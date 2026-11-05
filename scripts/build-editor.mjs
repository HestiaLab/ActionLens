// Bundles the CodeMirror 6 editor module that ActionLens's Blazor components drive through JS interop.
//
// esbuild is invoked through its JavaScript API rather than through `node_modules/.bin` because the
// offline seed path (scripts/seed-offline-node-modules.mjs) copies package directories without creating
// bin shims.
import { build, context } from 'esbuild';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const entry = path.join(repoRoot, 'src', 'ActionLens.App', 'ClientScripts', 'editor.ts');
const outfile = path.join(repoRoot, 'src', 'ActionLens.App', 'wwwroot', 'js', 'dist', 'actionlens-editor.js');

const options = {
  entryPoints: [entry],
  outfile,
  bundle: true,
  format: 'esm',
  target: ['es2022'],
  charset: 'utf8',
  minify: true,
  sourcemap: 'linked',
  legalComments: 'none',
};

if (process.argv.includes('--watch')) {
  const watcher = await context(options);
  await watcher.watch();
  console.log(`Watching ${path.relative(repoRoot, entry)} -> ${path.relative(repoRoot, outfile)}`);
} else {
  const result = await build(options);
  if (result && result.errors && result.errors.length > 0) {
    console.error(`esbuild reported ${result.errors.length} error(s).`);
    process.exit(1);
  }

  console.log(`Bundled ${path.relative(repoRoot, outfile)}.`);
}
