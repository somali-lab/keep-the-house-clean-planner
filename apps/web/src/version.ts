import versionText from '../../../version.txt?raw';

/** Official images use the release version; source builds receive an injected local build id. */
export const APP_VERSION = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : versionText.trim();
