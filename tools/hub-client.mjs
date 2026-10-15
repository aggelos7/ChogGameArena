// STORY-049 minimal SignalR client for the release smoke test (DEC-053): a WebSocket client (RFC 6455) on
// node:http/https and the SignalR JSON hub protocol, with Node built-ins only, so the smoke test needs no dependency.
// It does what the room hub needs: connect straight over WebSockets (the hub allows no other transport, so there is
// no negotiate step), invoke a hub method and await its completion, receive server invocations, and keep the
// connection alive with pings. `drop()` cuts the connection without a close handshake, the way a lost network does.
import { createHash, randomBytes } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';

const WEBSOCKET_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const RECORD_SEPARATOR = '\x1e';
const MessageType = { Invocation: 1, Completion: 3, Ping: 6, Close: 7 };

/** A client frame: FIN set, masked as RFC 6455 requires of clients. */
export function encodeFrame(opcode, payload = Buffer.alloc(0)) {
  const length = payload.length;
  const header = length < 126 ? Buffer.alloc(2) : length < 65536 ? Buffer.alloc(4) : Buffer.alloc(10);
  header[0] = 0x80 | opcode;
  if (length < 126) {
    header[1] = 0x80 | length;
  } else if (length < 65536) {
    header[1] = 0x80 | 126;
    header.writeUInt16BE(length, 2);
  } else {
    header[1] = 0x80 | 127;
    header.writeBigUInt64BE(BigInt(length), 2);
  }
  const mask = randomBytes(4);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}

/**
 * Reads whole frames off the front of `buffer`: returns the frames and what is left over. Handles 16- and 64-bit
 * lengths and masked frames (a server never masks, but tests may).
 */
export function decodeFrames(buffer) {
  const frames = [];
  let offset = 0;
  for (;;) {
    if (buffer.length - offset < 2) break;
    const fin = (buffer[offset] & 0x80) !== 0;
    const opcode = buffer[offset] & 0x0f;
    const masked = (buffer[offset + 1] & 0x80) !== 0;
    let length = buffer[offset + 1] & 0x7f;
    let headerLength = 2;
    if (length === 126) {
      if (buffer.length - offset < 4) break;
      length = buffer.readUInt16BE(offset + 2);
      headerLength = 4;
    } else if (length === 127) {
      if (buffer.length - offset < 10) break;
      length = Number(buffer.readBigUInt64BE(offset + 2));
      headerLength = 10;
    }
    const maskLength = masked ? 4 : 0;
    if (buffer.length - offset < headerLength + maskLength + length) break;
    const start = offset + headerLength + maskLength;
    const payload = Buffer.from(buffer.subarray(start, start + length));
    if (masked) {
      const mask = buffer.subarray(offset + headerLength, start);
      for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
    }
    frames.push({ fin, opcode, payload });
    offset = start + length;
  }
  return { frames, rest: buffer.subarray(offset) };
}

/**
 * Opens a hub connection. `url` is http(s)://host/path (ws/wss is implied); `cookie` authenticates it like any other
 * request and `ca` trusts a certificate the system doesn't. Resolves once the SignalR handshake has succeeded.
 */
export function connectHub(url, { cookie, ca, pingSeconds = 10 } = {}) {
  const key = randomBytes(16).toString('base64');
  const target = new URL(url);
  const secure = target.protocol === 'https:' || target.protocol === 'wss:';
  const request = secure ? httpsRequest : httpRequest;

  return new Promise((resolve, reject) => {
    const req = request(target, {
      agent: false,
      ...(secure && ca ? { ca } : {}),
      headers: {
        ...(cookie ? { cookie } : {}),
        connection: 'Upgrade',
        upgrade: 'websocket',
        'sec-websocket-version': '13',
        'sec-websocket-key': key,
      },
    });
    req.on('response', (response) => {
      response.resume();
      req.destroy();
      reject(new Error(`hub upgrade returned ${response.statusCode}`));
    });
    req.on('error', reject);
    req.on('upgrade', (response, socket, head) => {
      const expected = createHash('sha1').update(key + WEBSOCKET_GUID).digest('base64');
      if (response.headers['sec-websocket-accept'] !== expected) {
        socket.destroy();
        reject(new Error('hub upgrade returned a wrong Sec-WebSocket-Accept'));
        return;
      }
      resolve(new HubConnection(socket, head));
    });
    req.end();
  }).then(async (connection) => {
    await connection.handshake(pingSeconds);
    return connection;
  });
}

class HubConnection {
  #socket;
  #buffer = Buffer.alloc(0);
  #fragments = [];
  #text = '';
  #nextId = 1;
  #pending = new Map();
  #handlers = new Map();
  #handshake;
  #head;
  #ping;

  /** Resolves when the connection ends, for whatever reason. */
  closed;

  constructor(socket, head) {
    this.#socket = socket;
    let markClosed;
    this.closed = new Promise((resolve) => (markClosed = resolve));
    socket.on('data', (chunk) => this.#receive(chunk));
    socket.on('error', () => socket.destroy());
    socket.on('close', () => {
      clearInterval(this.#ping);
      const error = new Error('hub connection closed');
      this.#handshake?.reject(error);
      for (const { reject } of this.#pending.values()) reject(error);
      this.#pending.clear();
      markClosed();
    });
    this.#head = head;
  }

  handshake(pingSeconds) {
    const done = new Promise((resolve, reject) => (this.#handshake = { resolve, reject }));
    this.#sendText(JSON.stringify({ protocol: 'json', version: 1 }) + RECORD_SEPARATOR);
    this.#ping = setInterval(() => this.#send({ type: MessageType.Ping }), pingSeconds * 1000);
    this.#ping.unref();
    // Bytes that arrived with the upgrade response are read once the handshake's answer is awaited.
    if (this.#head?.length) this.#receive(this.#head);
    this.#head = undefined;
    return done;
  }

  /** Registers a handler for a method the server invokes, such as `RoomUpdated`. */
  on(target, handler) {
    this.#handlers.set(target, [...(this.#handlers.get(target) ?? []), handler]);
  }

  /** Invokes a hub method; resolves with its result, or rejects with the hub's error message. */
  invoke(target, ...args) {
    const invocationId = String(this.#nextId++);
    const result = new Promise((resolve, reject) => this.#pending.set(invocationId, { resolve, reject }));
    this.#send({ type: MessageType.Invocation, invocationId, target, arguments: args });
    return result;
  }

  /** A clean close: SignalR's close message, then the WebSocket close handshake. */
  close() {
    if (!this.#socket.destroyed) {
      this.#send({ type: MessageType.Close });
      this.#socket.write(encodeFrame(0x8));
      this.#socket.end();
    }
    return this.closed;
  }

  /** Cuts the connection with no goodbye, like a lost network. */
  drop() {
    this.#socket.destroy();
    return this.closed;
  }

  #send(message) {
    this.#sendText(JSON.stringify(message) + RECORD_SEPARATOR);
  }

  #sendText(text) {
    if (!this.#socket.destroyed) this.#socket.write(encodeFrame(0x1, Buffer.from(text, 'utf8')));
  }

  #receive(chunk) {
    const { frames, rest } = decodeFrames(Buffer.concat([this.#buffer, chunk]));
    this.#buffer = rest;
    for (const frame of frames) {
      if (frame.opcode === 0x8) {
        this.#socket.end();
      } else if (frame.opcode === 0x9) {
        this.#socket.write(encodeFrame(0xa, frame.payload));
      } else if (frame.opcode === 0x1 || frame.opcode === 0x0) {
        this.#fragments.push(frame.payload);
        if (frame.fin) {
          this.#text += Buffer.concat(this.#fragments).toString('utf8');
          this.#fragments = [];
          this.#messages();
        }
      }
    }
  }

  #messages() {
    let end;
    while ((end = this.#text.indexOf(RECORD_SEPARATOR)) >= 0) {
      const message = JSON.parse(this.#text.slice(0, end));
      this.#text = this.#text.slice(end + 1);
      this.#dispatch(message);
    }
  }

  #dispatch(message) {
    if (this.#handshake) {
      // The first message answers the handshake: {} or {"error": "..."}.
      const handshake = this.#handshake;
      this.#handshake = undefined;
      if (message.error) {
        handshake.reject(new Error(`hub handshake failed: ${message.error}`));
        this.#socket.destroy();
      } else {
        handshake.resolve();
      }
      return;
    }
    if (message.type === MessageType.Completion) {
      const pending = this.#pending.get(message.invocationId);
      this.#pending.delete(message.invocationId);
      if (message.error !== undefined) pending?.reject(new Error(message.error));
      else pending?.resolve(message.result);
    } else if (message.type === MessageType.Invocation) {
      for (const handler of this.#handlers.get(message.target) ?? []) handler(...(message.arguments ?? []));
    } else if (message.type === MessageType.Close) {
      this.#socket.end();
    }
  }
}
