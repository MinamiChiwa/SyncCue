const SECONDS = [5, 4, 3, 2, 1, 0];

// This plans audio-clock deadlines. Playback itself is performed by Web Audio,
// not by a DOM timer or focus/visibility event.
export class CueTimeline {
  constructor() {
    this.roundId = null;
    this.pending = new Map();
    this.fired = new Set();
    this.targetMs = null;
    this.leadMs = null;
  }

  update({ roundId, remainingMs, audioNowMs, leadMs, valid }) {
    const cancel = [];
    const schedule = [];
    for (const [key, cue] of this.pending) {
      if (cue.atMs <= audioNowMs) { this.fired.add(cue.seconds); this.pending.delete(key); }
    }
    if (roundId !== this.roundId) {
      cancel.push(...this.pending.keys()); this.pending.clear();
      this.roundId = roundId; this.fired.clear(); this.targetMs = null;
    }
    if (!valid || !roundId || !Number.isFinite(remainingMs)) {
      cancel.push(...this.pending.keys()); this.pending.clear(); this.targetMs = null;
      return { cancel, schedule };
    }
    const targetMs = audioNowMs + remainingMs;
    if (this.targetMs !== null && leadMs === this.leadMs && Math.abs(targetMs - this.targetMs) <= 25) return { cancel, schedule };
    cancel.push(...this.pending.keys()); this.pending.clear();
    this.targetMs = targetMs; this.leadMs = leadMs;
    for (const seconds of SECONDS) {
      if (this.fired.has(seconds)) continue;
      const delayMs = remainingMs - seconds * 1000;
      // Small packet/dispatch lateness at a boundary can play immediately;
      // older cues are discarded instead of producing a burst after reconnect.
      // Skipping an already-past boundary is not the same as playing it.
      // A later lead adjustment can move this unplayed boundary into the future.
      if (delayMs < -80) continue;
      const cue = { key: `${roundId}:${seconds}`, roundId, seconds, type: seconds === 0 ? 'hit' : 'tick', atMs: audioNowMs + Math.max(0, delayMs) };
      this.pending.set(cue.key, cue); schedule.push(cue);
    }
    return { cancel, schedule };
  }
}
