// Fallback dependency seeding for machines that cannot reach the npm registry.
//
// The supported path is `npm install` (package.json pins every direct dependency to an exact version).
// This script exists because ActionLens is developed on at least one machine with no outbound network at
// build time: it copies the same pinned versions out of trees already installed on the local disk into
// this repository's node_modules so that `npm run build:editor` can bundle without a registry.
//
// It is not a vendoring mechanism and nothing it copies is committed (node_modules/ is gitignored). It
// refuses to substitute a different version than package.json names, so a seeded tree and an installed
// tree bundle the same code.
import { cpSync, existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const targetRoot = path.join(repoRoot, 'node_modules');

// Trees to read from, searched in order for each package. ACTIONLENS_JS_SOURCE, when set, is searched
// first and is the supported way to point this script at a mirror on another machine.
const sources = [
  process.env.ACTIONLENS_JS_SOURCE,
  String.raw`C:\Users\World\.dsh\profiles\desktop\node_modules`,
  String.raw`C:\Users\World\AppData\Local\hermes\hermes-agent\node_modules`,
].filter(Boolean);

const manifest = JSON.parse(readFileSync(path.join(repoRoot, 'package.json'), 'utf8'));
const wanted = { ...(manifest.dependencies ?? {}), ...(manifest.devDependencies ?? {}) };

const copied = [];
const seen = new Set();

for (const [name, version] of Object.entries(wanted)) {
  copy(name, version, `package.json ${name}@${version}`);
}

console.log(`Seeded ${copied.length} packages into node_modules from local trees:`);
console.log(copied.map((entry) => `  ${entry}`).join('\n'));

function readManifest(packageDir) {
  const file = path.join(packageDir, 'package.json');
  if (!existsSync(file)) {
    return null;
  }

  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function locate(name) {
  for (const source of sources) {
    const directory = path.join(source, name);
    const found = readManifest(directory);
    if (found !== null) {
      return { source, directory, manifest: found };
    }
  }

  return null;
}

function copy(name, expectedVersion, reason) {
  const key = `${name}@${expectedVersion}`;
  if (seen.has(key)) {
    return;
  }

  seen.add(key);

  const hit = locate(name);
  if (hit === null) {
    console.error(`Missing ${name} in every seeded tree (needed for ${reason}). Run \`npm install\`.`);
    process.exit(1);
  }

  if (expectedVersion && hit.manifest.version !== expectedVersion) {
    console.error(
      `${name} in ${hit.source} is ${hit.manifest.version}; package.json pins ${expectedVersion} (${reason}).`,
    );
    process.exit(1);
  }

  cpSync(hit.directory, path.join(targetRoot, name), { recursive: true });
  copied.push(`${key}  <- ${hit.source}`);

  for (const [dependencyName] of Object.entries(hit.manifest.dependencies ?? {})) {
    // Transitive versions are whatever the tree that resolved them holds, at the version found there.
    const located = locate(dependencyName);
    if (located === null) {
      console.error(`Missing ${dependencyName} (a dependency of ${key}). Run \`npm install\`.`);
      process.exit(1);
    }

    copy(dependencyName, located.manifest.version, key);
  }

  // Bundlers ship the native binary as an optional dependency per platform; npm's postinstall picks it,
  // so a copied tree needs the one for this machine.
  const platformPackage = `@esbuild/${process.platform}-${process.arch}`;
  for (const name of Object.keys(hit.manifest.optionalDependencies ?? {})) {
    if (name === platformPackage) {
      const located = locate(name);
      if (located === null) {
        console.error(`Missing ${name} (the native binary for ${key}). Run \`npm install\`.`);
        process.exit(1);
      }

      copy(name, located.manifest.version, `${key} native binary`);
    }
  }
}
