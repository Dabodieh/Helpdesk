// Isolated visual review; uses the repository's existing Playwright installation.
// Run from the repository root: node docs/design/redesign/codex-prototype/capture.cjs
const { chromium } = require('../../../../src/web/node_modules/@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const out = path.join(__dirname, 'screenshots');
  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const report = [];
  for (const [width, height] of [[1440, 900], [1366, 768]]) {
    for (const theme of ['light', 'dark']) {
      for (const screen of (process.env.GRAPHITE_SCREENS || 'administration,department,mailboxes,workspace').split(',')) {
        const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        await page.goto(pathToFileURL(path.join(__dirname, screen + '.html')).href + '?theme=' + theme);
        await page.evaluate(() => document.fonts.ready);
        const layout = await page.evaluate(() => ({
          documentOverflow: document.documentElement.scrollWidth > innerWidth,
          mainOverflow: [...document.querySelectorAll('.page,.ticket-detail,.properties,.ticket-list')].filter(e => e.scrollWidth > e.clientWidth + 1).map(e => e.className),
          fontReady: document.fonts.check('14px Manrope'),
          theme: document.documentElement.dataset.theme,
        }));
        await page.screenshot({ path: path.join(out, `${screen}-${theme}-${width}x${height}.png`) });
        report.push({ screen, width, height, ...layout, errors });
        if (screen === 'mailboxes' && width === 1440) {
          await page.getByRole('button', { name: 'Add mailbox', exact: true }).click();
          await page.locator('#mailbox-address').fill('applications@northstar.example');
          await page.locator('#mailbox-name').fill('Applications Support');
          await page.screenshot({ path: path.join(out, `mailbox-drawer-${theme}-${width}x${height}.png`) });
        }
        await page.close();
      }
    }
  }
  const reportPath = path.join(__dirname, 'visual-review.json');
  const previous = process.env.GRAPHITE_SCREENS && fs.existsSync(reportPath) ? JSON.parse(fs.readFileSync(reportPath, 'utf8')) : [];
  const retained = previous.filter(p => !report.some(r => r.screen === p.screen && r.width === p.width && r.theme === p.theme));
  fs.writeFileSync(reportPath, JSON.stringify([...retained, ...report], null, 2) + '\n');
  console.log(JSON.stringify(report));
  await browser.close();
})().catch(e => { console.error(e.message); process.exit(1); });
