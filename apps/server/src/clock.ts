export interface Clock {
  now(): Date;
}

export const systemClock: Clock = {
  now: () => new Date(),
};

export interface FixedClock extends Clock {
  set(date: Date | string): void;
  advance(ms: number): void;
}

/** Clock that only moves when told to; used in tests and with APP_FAKE_NOW. */
export function fixedClock(start: Date | string): FixedClock {
  let current = new Date(start);
  return {
    now: () => new Date(current),
    set: (date) => {
      current = new Date(date);
    },
    advance: (ms) => {
      current = new Date(current.getTime() + ms);
    },
  };
}
