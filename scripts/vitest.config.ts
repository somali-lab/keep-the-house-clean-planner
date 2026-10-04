import { defineProject } from 'vitest/config';

export default defineProject({
  test: {
    name: 'scripts',
    environment: 'node',
    include: ['lib/**/*.test.ts'],
  },
});
