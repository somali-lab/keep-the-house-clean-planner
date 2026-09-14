import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.tsx';
import { initializeLanguage } from './i18n/runtime.ts';
import { initializeTheme } from './theme/ThemeProvider.tsx';
import './index.css';

const root = document.getElementById('root');
if (!root) throw new Error('Root element missing');

initializeLanguage();
initializeTheme();

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
