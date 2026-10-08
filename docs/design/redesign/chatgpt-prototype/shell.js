/* Graphite: independent, static Helpdesk visual prototype. No network/API access. */
(()=>{
 'use strict';
 const k='graphite-theme';
 let initial='light'; try { initial=localStorage.getItem(k)||'light'; }catch(e){}
 document.documentElement.dataset.theme=initial;
 const shapes={
 home:'<path d="M3 10.5 12 3l9 7.5V21H3z"/><path d="M9 21v-8h6v8"/>',
 grid:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
 inbox:'<path d="M4 4h16l2 9v7H2v-7z"/><path d="M2 13h6l2 3h4l2-3h6"/>',
 shield:'<path d="M12 2 20 5v6c0 5-3 8.5-8 11-5-2.5-8-6-8-11V5z"/><path d="m9 12 2 2 4-4"/>',
 org:'<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M9 8h.01M15 8h.01M9 12h.01M15 12h.01M9 16h6"/>',
 users:'<circle cx="9" cy="8" r="3"/><path d="M3 20v-2a6 6 0 0 1 12 0v2"/><path d="M16 5a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 5v1"/>',
 book:'<path d="M5 3h15v18H5a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3z"/><path d="M2 17a3 3 0 0 1 3-3h15"/>',
 search:'<circle cx="10.5" cy="10.5" r="7"/><path d="m16 16 5 5"/>',
 bell:'<path d="M5 17h14c-2-2-2-4-2-8a5 5 0 0 0-10 0c0 4 0 6-2 8z"/><path d="M10 21h4"/>',
 settings:'<circle cx="12" cy="12" r="3"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M19 5l-1.5 1.5m-11 11L5 19"/>',
 mail:'<rect x="2" y="5" width="20" height="14" rx="2"/><path d="m3 7 9 7 9-7"/>',
 bolt:'<path d="m13 2-9 12h8l-1 8 9-12h-8z"/>',
 clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l4 2"/>',
 chevron:'<path d="m9 5 7 7-7 7"/>',
 down:'<path d="m5 9 7 7 7-7"/>',
 plus:'<path d="M12 4v16M4 12h16"/>',
 arrow:'<path d="M4 12h16m-7-7 7 7-7 7"/>',
 check:'<path d="m4 12 5 5L20 6"/>',
 close:'<path d="M5 5 19 19M19 5 5 19"/>',
 sliders:'<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="2" fill="currentColor" stroke="none"/><circle cx="16" cy="17" r="2" fill="currentColor" stroke="none"/>',
 note:'<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h5"/>',
 lock:'<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
 attachment:'<path d="m19 12-8 8a5 5 0 0 1-7-7l9-9a3 3 0 0 1 4 4l-9 9a1.5 1.5 0 0 1-2-2l8-8"/>',
 send:'<path d="m22 2-9 20-3-8-8-3zM22 2 10 14"/>',
 more:'<circle cx="5" cy="12" r="1.2" fill="currentColor"/><circle cx="12" cy="12" r="1.2" fill="currentColor"/><circle cx="19" cy="12" r="1.2" fill="currentColor"/>',
 sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l2 2m10 10 2 2m0-14-2 2M7 17l-2 2"/>',
 moon:'<path d="M20 15A8 8 0 0 1 9 4a9 9 0 1 0 11 11z"/>',
 eye:'<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/>',
 alert:'<path d="m12 2 10 19H2z"/><path d="M12 9v5m0 3v.5"/>',
 filter:'<path d="M4 5h16l-6 7v6l-4 2v-8z"/>',
 refresh:'<path d="M21 8V3l-3 3a9 9 0 1 0 3 7"/>',
 edit:'<path d="m5 16-1 4 4-1L19 8l-3-3zM14 7l3 3"/>',
 layers:'<path d="m12 2 10 6-10 6L2 8zM2 12l10 6 10-6M2 16l10 6 10-6"/>',
 calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 2v6m10-6v6M3 10h18"/>',
 chat:'<path d="M3 4h18v14H9l-6 4z"/>',
 ticket:'<path d="M4 4h16v5a3 3 0 0 0 0 6v5H4v-5a3 3 0 0 0 0-6z"/><path d="M12 5v14"/>',
 copy:'<rect x="8" y="8" width="13" height="13" rx="1"/><path d="M16 8V3H3v13h5"/>',
 sparkle:'<path d="m12 2 2.5 7.5L22 12l-7.5 2.5L12 22l-2.5-7.5L2 12l7.5-2.5z"/>',
 activity:'<path d="M2 12h5l3-8 4 16 3-8h5"/>'
 };
 const ico=(n,size)=>`<svg class="icon" ${size?`style="width:${size}px;height:${size}px"`:''} viewBox="0 0 24 24" aria-hidden="true" focusable="false">${shapes[n]||shapes.grid}</svg>`;
 window.graphiteIcon=ico;
 const sideLink=(txt,ic,href,active,count)=>`<a class="side-link ${active?'active':''}" href="${href||'#'}" ${href?'':'data-preview="1"'} ${active?'aria-current="page"':''}>${ico(ic)}<span>${txt}</span>${count!==undefined?`<span class="nav-count">${count}</span>`:''}</a>`;
 function rail(current){let arr=[['workspace','inbox','workspace.html','Agent workspace'],['admin','org','admin.html','Administration'],['overview','grid','department-overview.html','Department setup'],['mailboxes','mail','department-mailboxes.html','Mailboxes']];return `<aside class="rail" aria-label="Primary navigation"><a class="brandmark" href="index.html" aria-label="Graphite prototype home"><span></span><span></span><span></span></a><nav class="rail-nav" aria-label="Areas">${arr.map(([k,i,u,t])=>`<a class="rail-link ${current===k?'active':''}" href="${u}" data-tip="${t}" aria-label="${t}" ${current===k?'aria-current="page"':''}>${ico(i)}</a>`).join('')}</nav><div class="rail-bottom"><button class="rail-link" type="button" data-preview="1" data-tip="Notifications" aria-label="Notifications">${ico('bell')}</button><button class="rail-link" type="button" data-theme-switch aria-label="Switch colour theme" data-tip="Toggle theme">${ico('moon')}</button><div class="rail-line"></div><span class="avatar dark" title="Dev Admin" aria-label="Dev Admin">DA</span></div></aside>`}
 function sidebar(screen){
 let dept=screen==='overview'||screen==='mailboxes';
 if(screen==='workspace')return '';
 if(dept)return `<aside class="sidebar" aria-label="Department navigation"><a class="back-link" href="admin.html">${ico('chevron')} All departments</a><div class="department-context"><span class="dept-icon it">${ico('org')}</span><strong>IT Support</strong><p>Department · IT</p></div><div class="nav-group">Department</div>${sideLink('Overview','grid','department-overview.html',screen==='overview')}<div class="nav-group">People</div>${sideLink('Teams','users',null,false)}${sideLink('Members','users',null,false)}${sideLink('Requester identities','shield',null,false)}<div class="nav-group">Communication</div>${sideLink('Mailboxes','mail','department-mailboxes.html',screen==='mailboxes')}${sideLink('Responses','chat',null,false)}<div class="nav-group">Ticket setup</div>${sideLink('Ticket types & fields','layers',null,false)}${sideLink('Request forms','ticket',null,false)}<div class="nav-group">Service management</div>${sideLink('Business hours','calendar',null,false)}${sideLink('SLA policies','clock',null,false)}${sideLink('Automations','bolt',null,false)}<div class="nav-group">Self service</div>${sideLink('Knowledge','book',null,false)}<div class="sidebar-foot">${ico('lock',15)} &nbsp;Department-scoped configuration</div></aside>`;
 return `<aside class="sidebar" aria-label="Administration navigation"><div class="sidebar-title">Administration</div><div class="nav-group">Organisation</div>${sideLink('Departments','org','admin.html',true,'04')}${sideLink('People & identities','users',null,false)}<div class="nav-group">Platform</div>${sideLink('System health','activity',null,false)}${sideLink('Audit log','shield',null,false)}<div class="sidebar-foot">Your access to departmental content depends on membership.</div></aside>`;
 }
 const heading=(screen)=>screen==='workspace'?'Work / My queue':screen==='admin'?'Administration / Departments':`Administration / IT Support / ${screen==='overview'?'Overview':'Mailboxes'}`;
 function boot(){let page=document.getElementById('page');if(!page)return;let screen=document.body.dataset.screen||'admin';let app=document.createElement('div');app.className='app';app.innerHTML=rail(screen)+`<header class="topbar"><a class="wordmark" href="index.html">helpdesk<span class="brand-period">.</span></a><span class="top-divider"></span><span class="org"><span class="org-mark">S</span> SAMH <span class="mini-chevron">${ico('down',13)}</span></span><span class="spacer"></span><button class="global-search" id="openCommand" type="button">${ico('search',16)}<span>Search across Helpdesk</span><span class="key">Ctrl K</span></button><button class="icon-btn" data-preview="1" aria-label="Notifications">${ico('bell')}</button><button class="icon-btn" data-theme-switch aria-label="Toggle theme">${ico('sun')}</button><span class="avatar rose" title="Dev Admin">DA</span></header><div class="${screen==='workspace'?'workarea':'body-layout'}">${sidebar(screen)}</div>`;
 page.before(app);app.querySelector('.body-layout,.workarea').append(page);
 document.querySelectorAll('[data-icon]').forEach(el=>{el.innerHTML=ico(el.dataset.icon,el.dataset.size?Number(el.dataset.size):undefined)});
 document.querySelectorAll('[data-theme-switch]').forEach(b=>{b.addEventListener('click',()=>{let next=document.documentElement.dataset.theme==='dark'?'light':'dark';document.documentElement.dataset.theme=next;try{localStorage.setItem(k,next)}catch(e){};setThemeIcons()})});
 function setThemeIcons(){document.querySelectorAll('[data-theme-switch]').forEach(b=>b.innerHTML=ico(document.documentElement.dataset.theme==='dark'?'sun':'moon'));}setThemeIcons();
 document.addEventListener('click',e=>{let el=e.target.closest('[data-preview]');if(el){e.preventDefault();window.graphiteToast('Visual concept only — no live action');}});
 const cd=document.getElementById('commandDialog');if(cd){document.getElementById('openCommand').onclick=()=>cd.showModal();document.querySelectorAll('[data-close-command]').forEach(b=>b.onclick=()=>cd.close());document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();cd.showModal()}})}else{document.getElementById('openCommand')?.addEventListener('click',()=>window.graphiteToast('Global search — concept preview only'));}
 }
 window.graphiteToast=msg=>{let t=document.getElementById('toast');if(!t){t=document.createElement('div');t.className='toast';t.id='toast';t.setAttribute('role','status');document.body.append(t);}t.textContent=msg;t.hidden=false;clearTimeout(window._gToast);window._gToast=setTimeout(()=>t.hidden=true,2600)};
 window.graphiteDialog=(id)=>document.getElementById(id)?.showModal();
 document.addEventListener('DOMContentLoaded',boot);
})();
