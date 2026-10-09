#!/usr/bin/env node
// STORY-004 runtime smoke test: starts an isolated Compose project on spare ports, waits
// for healthy services, writes data, recreates the containers and checks the data
// survived, then removes the project and its volumes. Does not touch the dev stack.
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const composeFile = join(dirname(fileURLToPath(import.meta.url)), '..', 'infrastructure', 'docker-compose.yml');
const env = {
  ...process.env,
  COMPOSE_PROJECT_NAME: `chog-smoke-${process.pid}`,
  POSTGRES_PASSWORD: randomBytes(12).toString('hex'),
  POSTGRES_PORT: process.env.SMOKE_POSTGRES_PORT ?? '15432',
  REDIS_PORT: process.env.SMOKE_REDIS_PORT ?? '16379',
};
const compose = (...args) =>
  execFileSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', ...args], { env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const psql = (sql) => compose('exec', '-T', 'postgres', 'psql', '-U', 'choggames', '-d', 'choggames', '-tAc', sql);
const step = (message) => console.log(`- ${message}`);

try {
  step('starting services and waiting for healthy');
  compose('up', '-d', '--wait');

  step('writing test data');
  psql("CREATE TABLE smoke (id int PRIMARY KEY, note text); INSERT INTO smoke VALUES (1, 'persisted');");
  compose('exec', '-T', 'redis', 'redis-cli', 'SET', 'smoke', 'persisted');

  step('removing and recreating containers (volumes kept)');
  compose('down');
  compose('up', '-d', '--wait');

  const pg = psql('SELECT note FROM smoke WHERE id = 1;');
  const redis = compose('exec', '-T', 'redis', 'redis-cli', 'GET', 'smoke');
  if (pg !== 'persisted') throw new Error(`PostgreSQL data lost after restart (got "${pg}")`);
  if (redis !== 'persisted') throw new Error(`Redis data lost after restart (got "${redis}")`);
  step('PostgreSQL and Redis data survived the restart');
  console.log('Compose smoke test passed.');
} catch (error) {
  console.error(`Compose smoke test failed: ${error.message}`);
  process.exitCode = 1;
} finally {
  compose('down', '-v', '--remove-orphans');
}
