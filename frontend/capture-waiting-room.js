/**
 * Captures the waiting-room screen. Kept separate from e2e-smoke.js because it is a
 * cosmetic capture, not an assertion, and should never fail the journey test.
 */
const puppeteer = require('puppeteer');

const APP = process.env.APP_URL || 'http://localhost:4300';
const API = process.env.API_URL || 'http://localhost:5099';

(async () => {
  const browser = await puppeteer.launch({
    headless: 'new',
    executablePath: process.env.CHROME_PATH || undefined,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 1 });

  try {
    const events = await (await fetch(`${API}/api/events`)).json();
    const event = events.find((e) => e.seatsRemaining > 0);

    // Queue a handful of buyers first so the position counter is not just "1".
    const fillerCount = 12;
    for (let i = 0; i < fillerCount; i++) {
      const login = await fetch(`${API}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: `filler-${i}-${Date.now()}@test.local`, displayName: `Filler ${i}` }),
      });
      const auth = await login.json();

      await fetch(`${API}/api/events/${event.id}/queue`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth.accessToken}` },
        body: '{}',
      });
    }
    console.log(`  queued ${fillerCount} fillers ahead`);

    await page.goto(`${APP}/sign-in?eventId=${event.id}`, { waitUntil: 'networkidle2' });
    await page.waitForSelector('#email');
    await page.type('#email', `waitingroom-${Date.now()}@example.com`);
    await page.type('#displayName', 'Waiting Room Demo');
    await page.click('button[type="submit"]');

    await page.waitForFunction(() => location.pathname.includes('/waiting-room'), { timeout: 25000 });
    await page.waitForFunction(
      () => {
        const el = document.querySelector('.text-6xl');
        return el && /\d/.test(el.textContent || '');
      },
      { timeout: 25000 },
    );

    // Hold the frame before admission so the waiting state is what gets captured.
    const position = await page.$eval('.text-6xl', (el) => el.textContent.trim());
    console.log(`  captured at position ${position}`);

    await page.screenshot({ path: 'e2e-waiting-room.png', fullPage: true });
    console.log('  screenshot saved: e2e-waiting-room.png');
  } catch (error) {
    console.error('  capture failed:', error.message);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();