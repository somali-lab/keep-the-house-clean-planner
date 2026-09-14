export type Language = 'nl' | 'en';

const STORAGE_KEY = 'huishoudplanner.language';
let currentLanguage: Language = 'nl';

function storedLanguage(): Language {
  try {
    const value = window.localStorage.getItem(STORAGE_KEY);
    if (value === 'nl' || value === 'en') return value;
  } catch {
    // The Dutch default remains available when localStorage is unavailable.
  }
  return 'nl';
}

export function initializeLanguage(): Language {
  return applyLanguage(storedLanguage());
}

export function applyLanguage(language: Language): Language {
  currentLanguage = language;
  document.documentElement.lang = language;
  return language;
}

export function persistLanguage(language: Language): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, language);
  } catch {
    // Switching still works for the current session.
  }
}

export function getLanguage(): Language {
  return currentLanguage;
}

export function getLocale(): 'nl-NL' | 'en-GB' {
  return currentLanguage === 'nl' ? 'nl-NL' : 'en-GB';
}
