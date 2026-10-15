import http from 'node:http';
import { randomBytes, randomInt, randomUUID } from 'node:crypto';
import { WebSocketServer, WebSocket } from 'ws';

const ROOM_TTL_MS = 30 * 60_000;
const MAX_MEMBERS = 24;
const messages = {
  BAD_MESSAGE: '消息格式错误。', BAD_NICKNAME: '昵称须为 1–20 个字符。',
  BAD_DURATION: '时长须为 1–600 秒。', FORBIDDEN: '此操作超出你的房间权限。',
  ROOM_NOT_FOUND: '房间不存在或服务已重启，请重新开房或检查房间码。',
  SESSION_EXPIRED: '身份已失效或已被踢出，请重新加入房间。',
  NOT_IN_ROOM: '请先开房或进房。', ALREADY_IN_ROOM: '请先离开当前房间。',
  ROOM_FULL: '房间已满（最多 24 人，包括保留的离线身份）。',
  MEMBER_NOT_FOUND: '成员不存在。', ROUND_ACTIVE: '倒计时正在进行，请先取消。',
  BAD_ROLE: '只能授予或撤销副房主。', SELF_ACTION: '不能对自己执行此操作。',
  SERVER_BUSY: '房间数量达到上限。', RATE_LIMIT: '操作过于频繁，请稍后重试。'
};
function fail(code) { const e = new Error(messages[code] || code); e.code = code; throw e; }
function nickname(value) {
  if (typeof value !== 'string' || !value.trim() || [...value.trim()].length > 20) fail('BAD_NICKNAME');
  return value.trim();
}
function duration(value) {
  if (!Number.isInteger(value) || value < 1000 || value > 600000) fail('BAD_DURATION');
  return value;
}

export async function createRoomServer({ port = 3131, host = '127.0.0.1' } = {}) {
  const rooms = new Map();
  const server = http.createServer((req, res) => {
    res.writeHead(req.url === '/health' ? 200 : 404, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(req.url === '/health' ? { ok: true, rooms: rooms.size, bindHost: host, tunnelLifecycle: 1 } : { error: 'not found' }));
  });
  const wss = new WebSocketServer({ server, maxPayload: 8192, perMessageDeflate: false });
  // ws forwards HTTP server errors onto its own EventEmitter. Handle that
  // event too, so startup's rejected listen promise remains catchable.
  wss.on('error', error => { if (server.listening) console.error('WebSocket server:', error.message); });
  const send = (ws, data) => { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(data)); };
  const view = room => ({
    code: room.code, durationMs: room.durationMs, hostId: room.hostId, round: room.round,
    members: [...room.members.values()].map(m => ({ id: m.id, nickname: m.nickname, role: m.role, ready: m.ready, connected: !!m.ws }))
  });
  function broadcast(room) {
    const data = { type: 'state', room: view(room), serverNowMs: performance.now() };
    for (const m of room.members.values()) send(m.ws, data);
    for (const watcher of room.watchers) send(watcher, data);
  }
  function deleteRoom(room) {
    for (const watcher of room.watchers) { send(watcher, { type: 'room-closed' }); watcher.watchingRoom = null; }
    room.watchers.clear(); rooms.delete(room.code);
  }
  function detach(ws, remove = false) {
    const { room, member } = ws;
    if (!room || member.ws !== ws) return;
    member.ws = null; member.ready = false;
    if (remove) {
      room.members.delete(member.id);
      if (room.hostId === member.id && room.members.size) {
        const next = [...room.members.values()].sort((a, b) => Number(!!b.ws) - Number(!!a.ws))[0];
        next.role = 'host'; room.hostId = next.id;
      }
    }
    ws.room = null; ws.member = null;
    room.touched = performance.now();
    if (!room.members.size) deleteRoom(room);
    else broadcast(room);
  }
  function attach(ws, room, member) {
    const old = member.ws;
    member.ws = ws; ws.room = room; ws.member = member;
    if (old && old !== ws) old.close(4001, 'Session resumed elsewhere');
    room.touched = performance.now();
    send(ws, { type: 'session', code: room.code, memberId: member.id, token: member.token });
    broadcast(room);
  }
  function handle(ws, msg) {
    if (!msg || typeof msg !== 'object' || Array.isArray(msg) || typeof msg.type !== 'string') fail('BAD_MESSAGE');
    if (msg.type === 'sync') {
      if (!Number.isInteger(msg.id)) fail('BAD_MESSAGE');
      send(ws, { type: 'sync', id: msg.id, serverNowMs: performance.now() }); return;
    }
    if (msg.type === 'watch-room') {
      if (ws.room || ws.watchingRoom) fail('ALREADY_IN_ROOM');
      const room = rooms.get(typeof msg.code === 'string' ? msg.code.trim().toUpperCase() : '');
      if (!room) fail('ROOM_NOT_FOUND');
      const member = [...room.members.values()].find(m => typeof msg.token === 'string' && m.token === msg.token);
      if (!member || member.role !== 'host') fail('FORBIDDEN');
      ws.watchingRoom = room; room.watchers.add(ws);
      send(ws, { type: 'watching', memberId: member.id, code: room.code });
      send(ws, { type: 'state', room: view(room), serverNowMs: performance.now() });
      return;
    }
    if (['create', 'join', 'resume'].includes(msg.type)) {
      if (ws.room) fail('ALREADY_IN_ROOM');
      let room;
      if (msg.type === 'create') {
        const name = nickname(msg.nickname);
        const ms = duration(msg.durationMs ?? 10000);
        if (rooms.size >= 1000) fail('SERVER_BUSY');
        let code;
        do { code = Array.from({ length: 6 }, () => 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'[randomInt(31)]).join(''); } while (rooms.has(code));
        room = { code, durationMs: ms, hostId: null, members: new Map(), watchers: new Set(), round: null, touched: performance.now() };
        rooms.set(code, room);
        const member = { id: randomUUID(), token: randomBytes(24).toString('hex'), nickname: name, role: 'host', ready: false, ws: null };
        room.hostId = member.id; room.members.set(member.id, member); attach(ws, room, member);
      } else {
        if (typeof msg.code !== 'string') fail('BAD_MESSAGE');
        room = rooms.get(msg.code.trim().toUpperCase());
        if (!room) fail('ROOM_NOT_FOUND');
        if (msg.type === 'resume') {
          const member = [...room.members.values()].find(m => typeof msg.token === 'string' && m.token === msg.token);
          if (!member) fail('SESSION_EXPIRED');
          attach(ws, room, member);
        } else {
          const name = nickname(msg.nickname);
          if (room.members.size >= MAX_MEMBERS) fail('ROOM_FULL');
          const member = { id: randomUUID(), token: randomBytes(24).toString('hex'), nickname: name, role: 'member', ready: false, ws: null };
          room.members.set(member.id, member); attach(ws, room, member);
        }
      }
      return;
    }
    const { room, member } = ws;
    if (!room || member.ws !== ws) fail('NOT_IN_ROOM');
    const hostOnly = ['kick', 'transfer', 'role', 'close-room'];
    if (hostOnly.includes(msg.type) && member.role !== 'host') fail('FORBIDDEN');
    if (['duration', 'start', 'cancel'].includes(msg.type) && !['host', 'cohost'].includes(member.role)) fail('FORBIDDEN');
    switch (msg.type) {
      case 'nickname': member.nickname = nickname(msg.nickname); break;
      case 'ready':
        if (typeof msg.ready !== 'boolean') fail('BAD_MESSAGE');
        member.ready = msg.ready; break;
      case 'duration': room.durationMs = duration(msg.durationMs); break;
      case 'start': {
        // Same room may start again once the previous hit moment has passed.
        if (room.round && performance.now() < room.round.targetServerMs) fail('ROUND_ACTIVE');
        for (const m of room.members.values()) m.ready = false;
        room.round = { id: randomUUID(), targetServerMs: performance.now() + room.durationMs };
        break;
      }
      case 'cancel': {
        room.round = null;
        break;
      }
      case 'leave': detach(ws, true); send(ws, { type: 'left' }); return;
      case 'close-room':
        for (const m of room.members.values()) {
          send(m.ws, { type: 'room-closed', message: '房主已关闭房间。' });
          if (m.ws) { m.ws.room = null; m.ws.member = null; }
        }
        deleteRoom(room); return;
      case 'kick': case 'transfer': case 'role': {
        const target = room.members.get(msg.memberId);
        if (!target) fail('MEMBER_NOT_FOUND');
        if (target === member) fail('SELF_ACTION');
        if (msg.type === 'kick') {
          const socket = target.ws;
          if (socket) { socket.room = null; socket.member = null; }
          room.members.delete(target.id);
          send(socket, { type: 'kicked', code: 'KICKED', message: '你已被房主移出房间。' });
        } else if (msg.type === 'transfer') {
          member.role = 'member'; target.role = 'host'; room.hostId = target.id;
        } else {
          if (!['member', 'cohost'].includes(msg.role)) fail('BAD_ROLE');
          target.role = msg.role;
        }
        break;
      }
      default: fail('BAD_MESSAGE');
    }
    room.touched = performance.now(); broadcast(room);
  }
  wss.on('connection', ws => {
    ws.alive = true; ws.rateStart = performance.now(); ws.rateCount = 0;
    ws.on('pong', () => { ws.alive = true; });
    ws.on('error', () => {});
    ws.on('message', bytes => {
      try {
        if (performance.now() - ws.rateStart >= 10000) { ws.rateCount = 0; ws.rateStart = performance.now(); }
        if (++ws.rateCount > 120) fail('RATE_LIMIT');
        let msg;
        try { msg = JSON.parse(bytes.toString()); } catch { fail('BAD_MESSAGE'); }
        handle(ws, msg);
      } catch (e) {
        if (!e.code) console.error('room handler:', e);
        send(ws, { type: 'error', code: e.code || 'BAD_MESSAGE', message: e.code ? e.message : messages.BAD_MESSAGE });
      }
    });
    ws.on('close', () => { if (ws.watchingRoom) ws.watchingRoom.watchers.delete(ws); detach(ws); });
  });
  const heartbeat = setInterval(() => {
    for (const ws of wss.clients) {
      if (!ws.alive) ws.terminate();
      else { ws.alive = false; ws.ping(); }
    }
    for (const room of rooms.values()) {
      if (room.round && performance.now() > room.round.targetServerMs + 1200) {
        room.round = null;
        room.touched = performance.now();
        broadcast(room);
      }
      if (![...room.members.values()].some(m => m.ws) && performance.now() - room.touched > ROOM_TTL_MS) deleteRoom(room);
    }
  }, 15000);
  heartbeat.unref();
  try {
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, host, resolve); });
  } catch (error) { clearInterval(heartbeat); wss.close(); throw error; }
  const address = server.address();
  return {
    url: `ws://${host === '0.0.0.0' ? '127.0.0.1' : host}:${address.port}`,
    port: address.port,
    async close() {
      clearInterval(heartbeat);
      for (const ws of wss.clients) ws.terminate();
      await new Promise(resolve => wss.close(resolve));
      await new Promise(resolve => server.close(resolve));
    }
  };
}
