import type { Dispatch, SetStateAction } from 'react';
import type { RegisterSWOptions } from 'vite-plugin-pwa/types';

const setFalse: Dispatch<SetStateAction<boolean>> = () => undefined;

/** Service workers stay disabled in component tests unless a test mocks this module explicitly. */
export function useRegisterSW(_options?: RegisterSWOptions) {
  return {
    needRefresh: [false, setFalse] as const,
    offlineReady: [false, setFalse] as const,
    updateServiceWorker: async (_reloadPage?: boolean) => undefined,
  };
}
