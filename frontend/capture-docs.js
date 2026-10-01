/**
 * Captures the screens used in the GitHub README.
 * Run with both servers up: node capture-docs.js
 */
const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const APP = process.env.APP_URL || 'http://localhost:4300';
const API = process.env.API_URL || 'http://localhost:5099';
const OUT = path.join(__dirname, 'docs', 'screenshots');
const PASSWORD = 'correct-horse-battery';

fs.mkdirSync(OUT, { recursive: true });

const log = (...a) => console.log('  ', ...a);
const shot = async (page, name) => {
  await page.screenshot({ path: path.join(OUT, name) });
  log('saved', name);
};

(async () => {
  const browser = await puppeteer.launch({
    headless: 'new',
    executablePath: process.env.CHROME_PATH || undefined,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  const events = await (await fetch(`${API}/api/events`)).json();
  const event = events.find((e) => e.seatsRemaining > 10);
  log('using event:', event.name);

  // A signed-in page reused for every shot.
  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 1 });

  page.on('console', (m) => {
    if (m.type() === 'error') {
      log('[browser]', m.text().slice(0, 160));
    }
  });
  page.on('response', async (res) => {
    if (res.url().includes('/api/') && res.status() >= 400) {
      log('[http]', res.status(), res.url().replace(APP, ''));
    }
  });

  // ---- 1. Sign-in -----------------------------------------------------------
  // Carry eventId so sign-in routes straight into the waiting room.
  await page.goto(`${APP}/sign-in?eventId=${event.id}`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('#email');
  await page.type('#email', `docs-${Date.now()}@example.com`);
  await page.type('#displayName', 'Alex Smith');
  await page.type('#password', PASSWORD);
  await shot(page, '1-sign-in.png');

  // ---- 2. Sign in and enter the queue --------------------------------------
  await page.click('button[type="submit"]');
  await page.waitForFunction(() => location.pathname.includes('/waiting-room'), {
    timeout: 25000,
  });

  // ---- 3. Queue company, then the waiting room ------------------------------
  // NOTE: the catalogue is captured at the END of this script. Opening a second
  // tab here would background the waiting-room tab, and a backgrounded tab can
  // miss the SignalR admission push, so the journey runs in one uninterrupted tab.
  // Give the queue some company so the position is not always 1.
  for (let i = 0; i < 11; i++) {
    const r = await fetch(`${API}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: `filler-${i}-${Date.now()}@t.local`,
        displayName: `Filler ${i}`,
        password: PASSWORD,
      }),
    });
    const a = await r.json();
    await fetch(`${API}/api/events/${event.id}/queue`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${a.accessToken}` },
      body: '{}',
    });
  }
  await page.bringToFront();

  await page.waitForFunction(
    () => {
      const el = document.querySelector('.text-6xl');
      return el && /\d/.test(el.textContent || '');
    },
    { timeout: 30000 },
  );
  await shot(page, '3-waiting-room.png');
  log('captured while position is shown');

  // ---- 4. Seat map with a multi-seat selection -----------------------------
  try {
    await page.waitForFunction(() => location.pathname.includes('/checkout'), {
      timeout: 60000,
    });
  } catch (err) {
    log('did not reach /checkout; landed on:', page.url());
    await shot(page, 'debug-landed.png');
    throw err;
  }
  await page.waitForSelector('button[aria-label^="Seat"]', { timeout: 20000 });

  const labels = await page.evaluate(() =>
    [...document.querySelectorAll('button[aria-label^="Seat"]')]
      .filter((b) => !b.disabled)
      .slice(0, 4)
      .map((b) => b.getAttribute('aria-label')),
  );
  for (const label of labels) {
    const sel = `button[aria-label="${label}"]`;
    await page.click(sel);
    await page.waitForFunction(
      (s) => document.querySelector(s)?.getAttribute('aria-pressed') === 'true',
      { timeout: 8000 },
      sel,
    );
  }
  await shot(page, '4-seat-selection.png');
  log(`selected ${labels.length} seats`);

  // ---- 5. Held basket -------------------------------------------------------
  await page.evaluate(() => {
    [...document.querySelectorAll('button')]
      .find((b) => b.textContent.includes('seat(s)') && b.textContent.includes('Hold'))
      ?.click();
  });
  await page.waitForFunction(() => document.body.textContent.includes('Seats held for you'), {
    timeout: 25000,
  });
  await shot(page, '5-basket-held.png');

  // ---- 6. Payment -----------------------------------------------------------
  await page.evaluate(() => {
    [...document.querySelectorAll('button')]
      .find((b) => b.textContent.includes('Go to payment'))
      ?.click();
  });
  await page.waitForFunction(() => location.pathname.includes('/payment'), { timeout: 15000 });
  await page.waitForSelector('#cardNumber', { timeout: 15000 });
  await page.type('#cardName', 'Alex Smith');
  await page.type('#cardNumber', '4242424242424242');
  await shot(page, '6-payment.png');

  // ---- 7. Receipt -----------------------------------------------------------
  await page.evaluate(() => {
    [...document.querySelectorAll('button')]
      .find((b) => b.textContent.trim().startsWith('Pay '))
      ?.click();
  });
  await page.waitForFunction(() => document.body.textContent.includes('Payment complete'), {
    timeout: 25000,
  });
  await shot(page, '7-receipt.png');

  // ---- 8. Catalogue (captured last, in a fresh tab) ------------------------
  const eventsPage = await browser.newPage();
  await eventsPage.setViewport({ width: 1280, height: 900 });
  await eventsPage.goto(`${APP}/events`, { waitUntil: 'networkidle2' });
  await eventsPage.waitForFunction(() => document.querySelectorAll('article').length > 0, {
    timeout: 20000,
  });
  await shot(eventsPage, '2-catalogue.png');
  await eventsPage.close();

  await browser.close();
  log('all screenshots captured');
})();