import versionText from '../../../version.txt?raw';

/** Release Please updates version.txt in the release PR; the web build embeds that exact value. */
export const APP_VERSION = versionText.trim();
