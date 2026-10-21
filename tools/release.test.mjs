// STORY-049 release checklist and smoke test checks (DEC-053). The hub client is exercised against an in-process
// WebSocket server that speaks the SignalR JSON protocol; the smoke test itself runs against a deployment
// (`npm run release:verify`, and inside `npm run deploy:verify`).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { connectHub, decodeFrames, encodeFrame } from './hub-client.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(join(root, p), 'utf8');
const RS = '\x1e';

/** A server frame: unmasked, optionally split into a text frame and continuations of `chunk` bytes. */
function serverFrames(text, chunk = Infinity) {
  const payload = Buffer.from(text, 'utf8');
  const parts = [];
  for (let i = 0; i < payload.length; i += chunk) parts.push(payload.subarray(i, i + chunk));
  return Buffer.concat(parts.map((part, i) => {
    const last = i === parts.length - 1;
    const length = part.length;
    const header = length < 126 ? Buffer.alloc(2) : length < 65536 ? Buffer.alloc(4) : Buffer.alloc(10);
    header[0] = (last ? 0x80 : 0) | (i === 0 ? 0x1 : 0x0);
    if (length < 126) header[1] = length;
    else if (length < 65536) (header[1] = 126), header.writeUInt16BE(length, 2);
    else (header[1] = 127), header.writeBigUInt64BE(BigInt(length), 2);
    return Buffer.concat([header, part]);
  }));
}

/**
 * A fake room hub: checks the session cookie, answers the handshake, completes `SubscribeRoom` (a large snapshot,
 * sent in fragments) or fails it, and pushes `RoomUpdated` when the client invokes `Poke`.
 */
async function fakeHub(t) {
  const received = [];
  const sockets = new Set();
  const server = createServer((_, response) => response.writeHead(404).end());
  server.on('upgrade', (request, socket) => {
    sockets.add(socket);
    if (request.headers.cookie !== 'chog_session=good') {
      socket.end('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n');
      return;
    }
    const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let buffer = Buffer.alloc(0);
    let text = '';
    const send = (message, chunk) => socket.write(serverFrames(JSON.stringify(message) + RS, chunk));
    socket.on('error', () => {});
    socket.on('data', (data) => {
      const { frames, rest } = decodeFrames(Buffer.concat([buffer, data]));
      buffer = rest;
      for (const frame of frames) {
        if (frame.opcode === 0x8) {
          socket.end();
          continue;
        }
        if (frame.opcode !== 0x1) continue;
        text += frame.payload.toString('utf8');
        let end;
        while ((end = text.indexOf(RS)) >= 0) {
          const message = JSON.parse(text.slice(0, end));
          text = text.slice(end + 1);
          received.push(message);
          if (message.protocol === 'json') {
            socket.write(serverFrames(`{}${RS}`));
            // A ping from the server, which the client must answer with a pong frame.
            socket.write(Buffer.from([0x89, 0x01, 0x2a]));
          } else if (message.type === 1 && message.target === 'SubscribeRoom' && message.arguments[0] === 'ABCDEF') {
            send({ type: 3, invocationId: message.invocationId, result: { code: 'ABCDEF', version: 3, pad: 'x'.repeat(70_000) } }, 30_000);
          } else if (message.type === 1 && message.target === 'SubscribeRoom') {
            send({ type: 3, invocationId: message.invocationId, error: 'HubException: room_not_found' });
          } else if (message.type === 1 && message.target === 'Poke') {
            send({ type: 1, target: 'RoomUpdated', arguments: [{ code: 'ABCDEF', version: 4 }] });
            send({ type: 3, invocationId: message.invocationId, result: null });
          }
        }
      }
    });
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  // Upgraded sockets are no longer the server's to close; they are destroyed first.
  t.after(() => {
    for (const socket of sockets) socket.destroy();
    return new Promise((resolve) => server.close(resolve));
  });
  return { url: `http://127.0.0.1:${server.address().port}/api/hubs/rooms`, received };
}

test('frames round-trip with 7-bit, 16-bit and 64-bit lengths, masked as a client must', () => {
  for (const size of [0, 125, 126, 65535, 65536, 70_000]) {
    const payload = Buffer.alloc(size, 'a');
    const frame = encodeFrame(0x1, payload);
    assert.equal(frame[1] & 0x80, 0x80, 'client frames are masked');
    const { frames, rest } = decodeFrames(frame);
    assert.equal(frames.length, 1);
    assert.equal(rest.length, 0);
    assert.ok(frames[0].fin);
    assert.deepEqual(frames[0].payload, payload);
  }
});

test('a partial frame waits for the rest', () => {
  const frame = encodeFrame(0x1, Buffer.from('hello'));
  const first = decodeFrames(frame.subarray(0, 4));
  assert.equal(first.frames.length, 0);
  const second = decodeFrames(Buffer.concat([first.rest, frame.subarray(4)]));
  assert.equal(second.frames[0].payload.toString(), 'hello');
});

test('the hub client handshakes, invokes, gets fragmented results, pushes and errors, and answers pings', async (t) => {
  const hub = await fakeHub(t);
  const connection = await connectHub(hub.url, { cookie: 'chog_session=good' });
  const pushed = [];
  connection.on('RoomUpdated', (room) => pushed.push(room));

  const snapshot = await connection.invoke('SubscribeRoom', 'ABCDEF');
  assert.equal(snapshot.version, 3);
  assert.equal(snapshot.pad.length, 70_000);

  await connection.invoke('Poke');
  assert.deepEqual(pushed, [{ code: 'ABCDEF', version: 4 }]);

  await assert.rejects(connection.invoke('SubscribeRoom', 'ZZZZZZ'), /room_not_found/);
  assert.deepEqual(hub.received[0], { protocol: 'json', version: 1 });
  await connection.close();
});

test('the hub client refuses an unauthorized upgrade and rejects pending calls when the connection drops', async (t) => {
  const hub = await fakeHub(t);
  await assert.rejects(connectHub(hub.url, { cookie: 'chog_session=bad' }), /returned 401/);

  const connection = await connectHub(hub.url, { cookie: 'chog_session=good' });
  const pending = connection.invoke('NeverAnswered');
  await connection.drop();
  await assert.rejects(pending, /closed/);
});

test('release:verify runs the release smoke test, and deploy:verify runs it against the production stack', () => {
  const pkg = JSON.parse(read('package.json'));
  assert.equal(pkg.scripts['release:verify'], 'node tools/release-smoke.mjs');
  const deploy = read('tools/deploy-smoke.mjs');
  assert.match(deploy, /runReleaseSmoke\(/);
  assert.match(deploy, /CHOG_LIFE_STATUS: 'Available'/);
  assert.match(deploy, /CHOG_RACE_STATUS: 'Available'/);
  assert.match(read('infrastructure/production/docker-compose.yml'), /Games__Items__1__Status: \$\{CHOG_RACE_STATUS:-ComingSoon\}/);
  assert.match(read('infrastructure/production/docker-compose.yml'), /Games__Items__2__Status: \$\{CHOG_ROYALE_STATUS:-ComingSoon\}/);
});

test('the release checklist covers every area the story names and the rollback drill', () => {
  const checklist = read('docs/release-checklist.md');
  for (const heading of ['Guests', 'Wallet', 'NFT', '2–6 players', 'Reconnect', 'Finish', 'Chog Race', 'Rollback']) {
    assert.match(checklist, new RegExp(`^##.*${heading}`, 'mi'), `the checklist needs a section on ${heading}`);
  }
  for (const command of ['npm run release:verify', 'npm run deploy:verify', 'npm run backup:verify', 'IMAGE_TAG=']) {
    assert.ok(checklist.includes(command), `the checklist must use ${command}`);
  }
  assert.match(checklist, /CHOG_LIFE_STATUS=Available/);
  assert.match(checklist, /CHOG_RACE_STATUS=Available/);
  assert.match(checklist, /never.*production/i, 'the checklist must keep the smoke test off production');
  assert.match(read('docs/deployment.md'), /release-checklist\.md/);
});
