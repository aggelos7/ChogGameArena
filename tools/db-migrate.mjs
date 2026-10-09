#!/usr/bin/env node
// Applies EF Core migrations with the pinned local dotnet-ef tool (backend/.config).
// Connection string: Database__ConnectionString env var, else the API's dotnet user-secrets.
// It is passed to dotnet-ef through the environment, never on the command line.
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const backend = join(dirname(fileURLToPath(import.meta.url)), '..', 'backend');
const run = (args, env = process.env) => execFileSync('dotnet', args, { cwd: backend, env, stdio: 'inherit' });

function fromUserSecrets() {
  try {
    const output = execFileSync('dotnet', ['user-secrets', 'list', '--project', 'src/ChogGames.Api'], { cwd: backend, encoding: 'utf8' });
    return output.match(/^Database:ConnectionString = (.+)$/m)?.[1].trim();
  } catch {
    return undefined;
  }
}

const connectionString = process.env.Database__ConnectionString?.trim() || fromUserSecrets();
if (!connectionString) {
  console.error(
    "No connection string found. Set Database__ConnectionString or run:\n" +
      '  dotnet user-secrets set "Database:ConnectionString" "<value>" --project backend/src/ChogGames.Api',
  );
  process.exit(1);
}

run(['tool', 'restore']);
run(
  ['ef', 'database', 'update', '--project', 'src/ChogGames.Infrastructure', '--startup-project', 'src/ChogGames.Infrastructure'],
  { ...process.env, Database__ConnectionString: connectionString, DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
);
