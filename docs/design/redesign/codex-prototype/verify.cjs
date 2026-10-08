// Focused prototype checks only. No production application or backend is exercised.
const { chromium, expect } = require('../../../../src/web/node_modules/@playwright/test');
const { createRequire } = require('node:module');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const webRequire = createRequire(path.resolve(__dirname, '../../../../src/web/package.json'));
const axePath = webRequire.resolve('axe-core/axe.min.js');
const output = { checks: [], accessibility: [], errors: [], externalRequests: [] };

(async () => {
 const browser = await chromium.launch({ channel: 'chrome', headless: true });
 const context = await browser.newContext({ viewport: { width: 1366, height: 768 } });
 const page = await context.newPage();
 page.on('pageerror', e => output.errors.push(e.message));
 page.on('request', r => { if (/^https?:/.test(r.url())) output.externalRequests.push(r.url()); });
 const go = async (name, theme='light') => { await page.goto(pathToFileURL(path.join(__dirname,name+'.html')).href+'?theme='+theme); await page.evaluate(()=>document.fonts.ready); };
 const pass = name => output.checks.push(name);

 if(process.argv.includes('--workspace-accessibility')) {
   const reportPath=path.join(__dirname,'interaction-review.json');
   const prior=JSON.parse(fs.readFileSync(reportPath,'utf8'));
   for(const theme of ['light','dark']) {
     await go('workspace',theme);
     await page.getByRole('tab',{name:'Public reply',exact:true}).focus();
     await page.keyboard.press('ArrowRight');
     await expect(page.getByRole('tab',{name:'Internal Note',exact:true})).toBeFocused();
     await page.addScriptTag({path:axePath});
     const results=await page.evaluate(async()=>await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa']}}));
     const record={screen:'workspace',theme,violations:results.violations.map(v=>({id:v.id,impact:v.impact}))};
     prior.accessibility=prior.accessibility.filter(a=>a.screen!=='workspace'||a.theme!==theme);
     prior.accessibility.push(record);
   }
   prior.checks.push('Corrected composer tab grouping and rechecked keyboard interaction in both themes');
   fs.writeFileSync(reportPath,JSON.stringify(prior,null,2)+'\n');
   console.log(JSON.stringify(prior));
   await browser.close();
   if(prior.accessibility.some(a=>a.violations.length))process.exitCode=1;
   return;
 }

 await go('administration');
 await page.getByLabel('Search Departments').fill('zz-no-department');
 await expect(page.getByText('No matching Departments')).toBeVisible();
 await page.getByRole('button',{name:'Clear filters'}).click();
 await expect(page.locator('#department-rows tr')).toHaveCount(4);
 await page.getByLabel('Filter Department status').selectOption('Inactive');
 await expect(page.locator('#department-rows tr')).toHaveCount(1);
 pass('Department search, status filtering and empty-state recovery');

 await page.getByRole('button',{name:'Switch to dark theme'}).click();
 await page.getByRole('link',{name:'Agent workspace',exact:true}).click();
 await expect(page.locator('html')).toHaveAttribute('data-theme','dark');
 pass('Navigation and theme persistence across files');
 await page.keyboard.press('Control+k');
 await expect(page.getByRole('dialog',{name:'Jump to a workspace'})).toBeVisible();
 await page.getByLabel('Search prototype screens').fill('mail');
 await expect(page.locator('#command-results a')).toHaveCount(1);
 await page.keyboard.press('Escape');
 pass('Keyboard screen search and Escape');

 await go('mailboxes');
 const add = page.getByRole('button',{name:'Add mailbox',exact:true});
 await add.click();
 await expect(page.locator('#mailbox-address')).toBeFocused();
 await page.getByLabel('Mailbox address',{exact:true}).fill('helpdesk@northstar.example');
 await page.getByLabel('Sender name',{exact:true}).fill('Duplicate example');
 await page.locator('#mailbox-form button[type=submit]').click();
 await expect(page.getByRole('alert')).toContainText('already configured');
 await page.getByLabel('Mailbox address',{exact:true}).fill('applications@northstar.example');
 await page.getByLabel('Sender name',{exact:true}).fill('Applications Support');
 await expect(page.locator('#sender-preview-name')).toHaveText('Applications Support');
 await page.locator('#mailbox-form button[type=submit]').click();
 await expect(page.locator('#mailbox-rows tr')).toHaveCount(4);
 await expect(page.locator('#mailbox-rows tr').last()).toContainText('Not verified');
 await expect(page.locator('#mailbox-rows tr').last()).toContainText('Not connected');
 pass('Mailbox add, live sender preview, duplicate validation and honest unconnected state');

 await page.getByRole('button',{name:'Edit Applications Support',exact:true}).click();
 await expect(page.locator('#mailbox-address')).toHaveAttribute('readonly','');
 await page.getByLabel('Sender name',{exact:true}).fill('Application Services');
 await page.getByRole('checkbox',{name:/Default for new tickets/}).check();
 await page.getByRole('button',{name:'Save changes'}).click();
 await expect(page.locator('#mailbox-rows .badge.brand')).toHaveCount(1);
 await expect(page.locator('#mailbox-rows tr').last()).toContainText('Default');
 await page.getByRole('button',{name:'Edit Application Services',exact:true}).click();
 await page.locator('#mailbox-state').selectOption('Inactive');
 await expect(page.getByText('Public replies will be blocked')).toBeVisible();
 await page.keyboard.press('Escape');
 await expect(page.getByRole('button',{name:'Edit Application Services',exact:true})).toBeFocused();
 pass('Mailbox editing, fixed address, unique default, deactivation warning and focus restoration');
 await page.getByRole('button',{name:'Review',exact:true}).first().click();
 await expect(page.getByRole('dialog')).toContainText('duplicate email');
 await expect(page.getByRole('dialog').getByRole('button',{name:/retry|resend/i})).toHaveCount(0);
 await page.keyboard.press('Escape');
 pass('Uncertain send review has no blind retry action');

 await go('workspace');
 await page.getByLabel('Write a public reply').fill('Public draft retained.');
 await page.getByRole('tab',{name:'Internal Note',exact:true}).click();
 await page.getByLabel('Write an Internal Note').fill('Private draft retained.');
 await page.getByRole('tab',{name:'Public reply',exact:true}).click();
 await expect(page.getByLabel('Write a public reply')).toHaveValue('Public draft retained.');
 await page.locator('[data-action="ticket:IT-2043"]').click();
 await expect(page.getByRole('heading',{name:'Access to the quarterly planning folder',exact:true})).toBeVisible();
 await expect(page.getByLabel('Write a public reply')).toHaveValue('');
 await page.locator('[data-action="ticket:IT-2048"]').click();
 await expect(page.getByLabel('Write a public reply')).toHaveValue('Public draft retained.');
 await page.getByRole('tab',{name:'Internal Note',exact:true}).click();
 await expect(page.getByLabel('Write an Internal Note')).toHaveValue('Private draft retained.');
 await page.getByRole('button',{name:'Add demo note',exact:true}).click();
 await expect(page.locator('.message.internal').last()).toContainText('Private draft retained.');
 await expect(page.locator('.message.internal').last()).toContainText('Local demo only');
 pass('Ticket selection and isolated per-ticket public/Internal Note drafts');
 pass('Local Internal Note submission has explicit no-send state');
 await page.getByRole('tab',{name:'Internal Note',exact:true}).focus();
 await page.keyboard.press('ArrowLeft');
 await expect(page.getByRole('tab',{name:'Public reply',exact:true})).toBeFocused();
 await expect(page.getByRole('tab',{name:'Public reply',exact:true})).toHaveAttribute('aria-selected','true');
 await page.getByRole('button',{name:'Add demo reply',exact:true}).click();
 await expect(page.locator('.conversation .message').last()).toContainText('Public draft retained.');
 pass('Reply/note tab keyboard navigation and local public reply');
 await page.getByLabel('Priority',{exact:true}).selectOption('Urgent');
 await expect(page.getByLabel('Priority',{exact:true})).toBeFocused();
 await expect(page.locator('.ticket-actions')).toContainText('Urgent priority');
 await page.getByRole('button',{name:'Resolve',exact:true}).click();
 await expect(page.getByLabel('Status',{exact:true})).toHaveValue('Resolved');
 await page.getByRole('button',{name:'Reopen',exact:true}).click();
 pass('Property edits, focus retention, resolve and reopen');
 await page.locator('[data-action="queue:unassigned"]').click();
 await expect(page.locator('#ticket-rows .ticket-row')).toHaveCount(1);
 await expect(page.locator('#ticket-detail h1')).toContainText('New starter equipment');
 await page.getByLabel('Search tickets').fill('zz-no-ticket');
 await expect(page.getByText('No matching tickets')).toBeVisible();
 await page.getByRole('button',{name:'Clear search'}).click();
 await expect(page.locator('#ticket-rows .ticket-row')).toHaveCount(1);
 pass('Queue selection, counts, search and empty-state recovery');

 for(const theme of ['light','dark']) {
   for(const screen of ['administration','department','mailboxes','workspace']) {
     await go(screen,theme);
     await page.addScriptTag({path:axePath});
     const results=await page.evaluate(async()=>await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa']}}));
     output.accessibility.push({screen,theme,violations:results.violations.map(v=>({id:v.id,impact:v.impact,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))}))});
   }
 }
 await go('index');
 await expect(page.locator('.screen-preview')).toHaveCount(4);
 const loaded=await page.locator('.screen-frame img').evaluateAll(imgs=>imgs.every(i=>i.complete&&i.naturalWidth>0));
 if(!loaded) throw new Error('A landing page preview image did not load.');
 pass('Landing page uses four real, locally loaded screenshots');
 await page.screenshot({path:path.join(__dirname,'screenshots','index-light-1366x768.png')});
 await browser.close();
 fs.writeFileSync(path.join(__dirname,'interaction-review.json'),JSON.stringify(output,null,2)+'\n');
 console.log(JSON.stringify(output));
 if(output.errors.length||output.externalRequests.length||output.accessibility.some(a=>a.violations.length)) process.exitCode=1;
})().catch(e=>{console.error(e.stack);process.exit(1);});
