/**
 * End-to-end smoke test against the running dev server + API.
 *
 * Drives the real buyer journey in a real browser:
 *   sign in -> waiting room (queue position visible) -> admitted -> checkout ->
 *   guard blocks /checkout before admission.
 *
 * Run with the API on :5099 and `ng serve` on :4300.
 */
const puppeteer = require('puppeteer');

const APP = process.env.APP_URL || 'http://localhost:4300';
const API = process.env.API_URL || 'http://localhost:5099';
const EMAIL = `browser-${Date.now()}@example.com`;
const PASSWORD = 'correct-horse-battery';

const log = (...args) => console.log('  ', ...args);

(async () => {
  const browser = await puppeteer.launch({
    headless: 'new',
    // Prefer the system Chrome; fall back to Puppeteer's bundled build.
    executablePath: process.env.CHROME_PATH || undefined,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  const page = await browser.newPage();
  const errors = [];

  page.on('pageerror', (err) => errors.push(`pageerror: ${err.message}`));
  page.on('console', (msg) => {
    if (msg.type() === 'error') {
      errors.push(`console: ${msg.text()}`);
    }
  });

  // Log every failing request so a 401 can be traced to an exact URL.
  page.on('response', async (res) => {
    if (res.status() >= 400) {
      const target = `${res.request().method()} ${res.url()}`;
      let detail = '';
      try {
        detail = (await res.text()).slice(0, 200);
      } catch {
        /* body unavailable */
      }
      errors.push(`http ${res.status()} ${target} :: ${detail}`);
    }
  });

  try {
    // ---- Fetch an event id directly from the API -------------------------------
    const events = await (await fetch(`${API}/api/events`)).json();
    const event = events.find((e) => e.seatsRemaining > 0);
    if (!event) {
      throw new Error('No seeded events available.');
    }
    log(`using event: ${event.name} (${event.id})`);

    // ---- 1. Guard must block /checkout with no token --------------------------
    await page.goto(`${APP}/checkout?eventId=${event.id}`, { waitUntil: 'networkidle2' });
    await new Promise((r) => setTimeout(r, 1500));
    const redirectedUrl = page.url();
    if (!redirectedUrl.includes('/sign-in')) {
      throw new Error(`Guard did not redirect an anonymous user: ${redirectedUrl}`);
    }
    log('PASS  guard redirected anonymous /checkout -> /sign-in');

    // ---- 2. Sign in ------------------------------------------------------------
    // Carry eventId through sign-in so the app routes straight into the queue.
    await page.goto(`${APP}/sign-in?eventId=${event.id}`, { waitUntil: 'networkidle2' });
    await page.waitForSelector('#email');
    await page.type('#email', EMAIL);
    await page.type('#displayName', 'Browser Tester');
    await page.type('#password', PASSWORD);
    await page.click('button[type="submit"]');

    await page.waitForFunction(() => location.pathname.includes('/waiting-room'), {
      timeout: 20000,
    });
    log('PASS  signed in and landed in /waiting-room');

    // ---- 3. Waiting room renders a live position -------------------------------
    await page.waitForFunction(
      () => {
        const el = document.querySelector('.text-6xl');
        return el && /\d/.test(el.textContent || '');
      },
      { timeout: 25000 },
    );
    const position = await page.$eval('.text-6xl', (el) => el.textContent.trim());
    log(`PASS  waiting room shows position ${position}`);

    // ---- 4. Worker admits the buyer -> auto-navigates to /checkout ------------
    await page.waitForFunction(() => location.pathname.includes('/checkout'), {
      timeout: 45000,
    });
    log('PASS  admitted via SignalR and auto-navigated to /checkout');

    // ---- 5. Seat map rendered ---------------------------------------------------
    await page.waitForFunction(
      () => document.querySelectorAll('button[aria-label^="Seat"]').length > 0,
      { timeout: 15000 },
    );
    const seatCount = await page.$$eval('button[aria-label^="Seat"]', (els) => els.length);
    log(`PASS  seat map rendered with ${seatCount} seats`);

    // ---- 6. Select THREE seats ---------------------------------------------
    // Click one at a time and wait for aria-pressed to flip, so Angular's change
    // detection settles between clicks.
    const wanted = 3;
    const labels = await page.evaluate(() =>
      [...document.querySelectorAll('button[aria-label^="Seat"]')]
        .filter((b) => !b.disabled)
        .slice(0, 3)
        .map((b) => b.getAttribute('aria-label')),
    );

    for (const label of labels) {
      const selector = `button[aria-label="${label}"]`;
      await page.click(selector);
      await page.waitForFunction(
        (sel) => document.querySelector(sel)?.getAttribute('aria-pressed') === 'true',
        { timeout: 10000 },
        selector,
      );
    }

    const selectedCount = await page.$$eval('button[aria-pressed="true"]', (els) => els.length);
    if (selectedCount !== wanted) {
      throw new Error(`Expected ${wanted} seats selected, got ${selectedCount}.`);
    }
    log(`PASS  selected ${selectedCount} seats: ${labels.join(', ')}`);

    // ---- 6b. Hold the whole basket in one order ----------------------------
    await page.evaluate(() => {
      [...document.querySelectorAll('button')]
        .find((b) => b.textContent.includes('seat(s)') && b.textContent.includes('Hold'))
        ?.click();
    });

    await page.waitForFunction(
      () => document.body.textContent.includes('Seats held for you'),
      { timeout: 20000 },
    );

    const chips = await page.$$eval('section li', (els) =>
      els.map((e) => e.textContent.trim()).filter((t) => /^[\d-]+$/.test(t)),
    );
    if (chips.length !== wanted) {
      throw new Error(`Expected ${wanted} seats held, got ${chips.length}: ${chips}`);
    }
    log(`PASS  held ${chips.length} seats in ONE order: ${chips.join(', ')}`);

    // ---- 6c. Payment page ----------------------------------------------------
    await page.evaluate(() => {
      [...document.querySelectorAll('button')]
        .find((b) => b.textContent.includes('Go to payment'))
        ?.click();
    });

    await page.waitForFunction(() => location.pathname.includes('/payment'), {
      timeout: 15000,
    });
    await page.waitForSelector('#cardNumber', { timeout: 15000 });
    log('PASS  reached the payment page');

    // ---- 6d. Pay -------------------------------------------------------------
    await page.type('#cardName', 'Browser Tester');
    await page.type('#cardNumber', '4242424242424242');

    await page.evaluate(() => {
      [...document.querySelectorAll('button')]
        .find((b) => b.textContent.trim().startsWith('Pay '))
        ?.click();
    });

    await page.waitForFunction(
      () => document.body.textContent.includes('Payment complete'),
      { timeout: 20000 },
    );
    log('PASS  payment complete — receipt shown');

    // ---- 7. Screenshot the completed receipt --------------------------------
    await page.screenshot({ path: 'e2e-checkout.png', fullPage: true });
    log('screenshot saved: e2e-checkout.png');

    // ---- Summary ---------------------------------------------------------------
    const ignorable = /favicon|ERR_|Failed to load resource/i;
    const realErrors = errors.filter((e) => !ignorable.test(e));

    log(`console/page errors: ${realErrors.length}`);
    realErrors.slice(0, 5).forEach((e) => log('   -', e));

    console.log('\nE2E PASSED');
  } catch (error) {
    console.error('\nE2E FAILED:', error.message);
    await page.screenshot({ path: 'e2e-failure.png', fullPage: true }).catch(() => {});
    console.error('console/page errors:', errors.slice(0, 10));
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
