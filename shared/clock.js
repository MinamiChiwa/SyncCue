// Both sides use monotonic clocks: operating-system calendar changes are irrelevant.
export class RoomClock {
  constructor(now = () => performance.now()) {
    this.now = now;
    this.reset();
  }

  reset() { this.samples = []; this.offset = null; this.rtt = null; }

  sample(sentMs, receivedMs, serverMs) {
    if (![sentMs, receivedMs, serverMs].every(Number.isFinite) || receivedMs < sentMs) return false;
    const rtt = receivedMs - sentMs;
    if (rtt > 2000) return false;
    this.samples.push({ rtt, offset: serverMs - (sentMs + receivedMs) / 2 });
    this.samples = this.samples.slice(-20);
    const best = this.samples.reduce((a, b) => a.rtt <= b.rtt ? a : b);
    this.offset = best.offset;
    this.rtt = best.rtt;
    return true;
  }

  serverNow() { return this.offset === null ? null : this.now() + this.offset; }
  remaining(targetServerMs, leadMs = 0) {
    const now = this.serverNow();
    return now === null ? null : targetServerMs - now - leadMs;
  }
}
