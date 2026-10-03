import versionText from '../../../version.txt?raw';

/** Official images use the release version; source builds receive an injected local build id. */
export const APP_VERSION = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : versionText.trim();
/** True only for published images built by the release workflow. */
export const APP_OFFICIAL_BUILD = typeof __APP_OFFICIAL_BUILD__ === 'boolean' ? __APP_OFFICIAL_BUILD__ : false;
/** ISO moment of the release an official image was built from; null for local builds. */
export const APP_RELEASE_DATE = typeof __APP_RELEASE_DATE__ === 'string' ? __APP_RELEASE_DATE__ : null;
/** Git ref whose LICENSE and CHANGELOG belong to this build. */
export const APP_SOURCE_REF = typeof __APP_SOURCE_REF__ === 'string' ? __APP_SOURCE_REF__ : 'main';
