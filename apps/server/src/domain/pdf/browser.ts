import { chromium, type Browser } from 'playwright';

/** One shared Chromium for the whole process: started on first use, reused, closed on shutdown. */
let launching: Promise<Browser> | null = null;

export function getBrowser(): Promise<Browser> {
  if (!launching) {
    launching = chromium
      .launch()
      .then((browser) => {
        browser.on('disconnected', () => {
          launching = null;
        });
        return browser;
      })
      .catch((err: unknown) => {
        launching = null;
        throw err;
      });
  }
  return launching;
}

export async function closeBrowser(): Promise<void> {
  const current = launching;
  launching = null;
  if (!current) return;
  try {
    await (await current).close();
  } catch {
    // Launch failed or the browser is already gone.
  }
}

/** Renders self-contained HTML to an A4 PDF. All network requests are blocked. */
export async function renderPdf(html: string, options: { landscape: boolean }): Promise<Buffer> {
  const browser = await getBrowser();
  const context = await browser.newContext();
  try {
    await context.route('**/*', (route) => route.abort());
    const page = await context.newPage();
    await page.setContent(html, { waitUntil: 'load' });
    return await page.pdf({
      format: 'A4',
      landscape: options.landscape,
      printBackground: true,
      margin: { top: '10mm', bottom: '10mm', left: '10mm', right: '10mm' },
    });
  } finally {
    await context.close();
  }
}
