import { createRoomServer } from './room-server.js';

const port = Number(process.env.PORT || 3131);
const host = process.env.HOST || '0.0.0.0';
try {
  const service = await createRoomServer({ port, host });
  console.log(`Sync Cue room server: ${service.url} (bind ${host})`);
  console.log('No game integration. Ctrl+C to stop. Rooms are held in memory.');
  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.once(signal, async () => { await service.close(); process.exit(0); });
  }
} catch (error) {
  console.error(`Server could not start: ${error.code || error.message}`);
  process.exitCode = 1;
}
