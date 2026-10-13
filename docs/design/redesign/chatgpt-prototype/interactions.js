/* In-browser prototype behaviour. No network requests or persistent business data. */
(()=>{'use strict';
 const $=id=>document.getElementById(id);
 document.querySelectorAll('[data-dialog-close]').forEach(b=>b.addEventListener('click',()=>$(b.dataset.dialogClose)?.close()));
 // Administration search, filter and illustration drawer.
 if($('deptRows')){
  const filter=()=>{const q=$('deptSearch').value.trim().toLowerCase(), s=$('deptFilter').value;let n=0;document.querySelectorAll('#deptRows tr').forEach(tr=>{let show=tr.dataset.dept.includes(q)&&(s==='all'||tr.dataset.status===s);tr.hidden=!show;if(show)n++});$('deptEmpty').hidden=n>0;$('deptCount').textContent=`Showing ${n} department${n===1?'':'s'}`};
  $('deptSearch').addEventListener('input',filter);$('deptFilter').addEventListener('change',filter);
  $('clearDept').addEventListener('click',()=>{$('deptSearch').value='';$('deptFilter').value='all';filter()});
  $('newDepartment').addEventListener('click',()=>$('newDeptDialog').showModal());
  $('newDeptForm').addEventListener('submit',e=>{e.preventDefault();if(!$('newDeptForm').reportValidity())return;$('newDeptDialog').close();window.graphiteToast('Prototype only — department not created')});
 }
 // Mailbox form: edit or add illustrated rows; explicit confirmation that nothing is saved.
 if($('mailboxRows')){
  let editing=null;
  const d=$('mailboxDialog'), email=$('mailEmail'), sender=$('mailSender');
  const updatePreview=()=>{$('senderPreview').textContent=sender.value.trim()||'Sender display name';$('emailPreview').textContent=(editing?.dataset.email||email.value.trim())||'shared@yourcompany.org'};
  [email,sender].forEach(i=>i.addEventListener('input',updatePreview));
  const open=tr=>{editing=tr||null;$('mailboxForm').reset();$('mailFormError').hidden=true;
   $('mailboxDialogTitle').textContent=tr?'Manage mailbox':'Add mailbox';$('mailboxDialogSub').textContent=tr?'Edit this address’s example settings.':'Connect an address to the IT Support department.';
   $('mailSave').textContent=tr?'Save changes (preview)':'Add mailbox (preview)';$('emailField').hidden=!!tr;$('emailReadOnly').hidden=!tr;$('mailActiveWrap').hidden=!tr;
   if(tr){$('fixedMailboxEmail').textContent=tr.dataset.email;sender.value=tr.dataset.sender;$('mailDefault').checked=tr.dataset.default==='true';$('mailActive').checked=tr.dataset.active==='true'}else{$('mailActive').checked=true;$('mailDefault').checked=!document.querySelector('#mailboxRows tr[data-default="true"]')}
   updatePreview();d.showModal();(tr?sender:email).focus();
  };
  $('addMailbox').addEventListener('click',()=>open(null));$('emptyAddMailbox').addEventListener('click',()=>open(null));
  $('mailboxRows').addEventListener('click',e=>{const b=e.target.closest('[data-edit-mailbox]');if(b)open(b.closest('tr'))});
  const render=tr=>{tr.querySelector('[data-sender-label]').textContent=tr.dataset.sender;
   let badge=tr.querySelector('[data-status-badge]');badge.className='badge'+(tr.dataset.active==='true'?' green':'');badge.textContent=tr.dataset.active==='true'?'Active':'Inactive';
   tr.querySelector('[data-default-badge]').hidden=tr.dataset.default!=='true';tr.querySelector('[data-connect-label]').textContent=tr.dataset.active==='true'?'Connection pending (preview)':'Not receiving mail';
  };
  $('mailboxForm').addEventListener('submit',e=>{e.preventDefault();let v=sender.value.trim();if(!v){$('mailFormError').hidden=false;$('mailFormError').textContent='Enter a sender display name.';sender.focus();return}
   if(!editing){let a=email.value.trim().toLowerCase();if(!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(a)){$('mailFormError').hidden=false;$('mailFormError').textContent='Enter a valid mailbox email address.';email.focus();return}
   if([...$('mailboxRows').children].some(tr=>tr.dataset.email===a)){$('mailFormError').hidden=false;$('mailFormError').textContent='This address is already shown.';email.focus();return}
   const tr=$('mailboxRows').firstElementChild.cloneNode(true);tr.dataset.email=a;tr.dataset.sender=v;tr.dataset.default=$('mailDefault').checked?'true':'false';tr.dataset.active='true';tr.querySelector('.mail-address').firstChild.textContent=a+' ';tr.querySelector('.cell-sub').textContent='New demonstration row';$('mailboxRows').append(tr);editing=tr;
   }else{editing.dataset.sender=v;editing.dataset.default=$('mailDefault').checked?'true':'false';editing.dataset.active=$('mailActive').checked?'true':'false'}
   if($('mailDefault').checked){[...$('mailboxRows').children].forEach(tr=>{if(tr!==editing)tr.dataset.default='false'})}
   [...$('mailboxRows').children].forEach(render);d.close();window.graphiteToast('Updated illustrative mailbox only — nothing saved or connected');
  });
 }
 // Workspace: queue scope changes list; ticket selection switches synthetic content, never edits a real ticket.
 if($('ticketRows')){
  const fixture={
   '14':['Priya Nair','priya.nair@example.test','VPN disconnects every 30 minutes','Hi, the VPN keeps dropping roughly every 30 minutes from my desk on floor 3. When I work on the ground floor it seems fine. Could someone check whether it is the laptop or the network?'],
   '13':['Tom Okafor','tom.okafor@example.test','New starter laptop needed on Monday','We have a new colleague joining on Monday. Could IT arrange a laptop and basic access before their start date?'],
   '12':['Mei Lin','mei.lin@example.test','Printer jam on third floor','The third floor printer displays a paper-jam message. We have checked the obvious tray but cannot see the stuck paper.'],
   '10':['Dan Whitfield','dan.whitfield@example.test','External emails bouncing','Several customers say their emails to our shared address have bounced. Can someone investigate quickly?'],
   '11':['Aisha Rahman','aisha.rahman@example.test','Finance shared mailbox access','Can you grant the Finance team access to our shared mailbox? We need to cover leave this week.']
  };
  let queue='mine',selected='14';const original=$('conversation').innerHTML;
  const status=()=>{let all=[...document.querySelectorAll('.ticket-row')];let q=$('ticketFilter').value.trim().toLowerCase(), visible=all.filter(b=>b.dataset.queues.split(' ').includes(queue)&&[b.dataset.title,b.dataset.requester,b.dataset.ticket].join(' ').toLowerCase().includes(q));all.forEach(b=>b.hidden=!visible.includes(b));$('queueCount').textContent=String(visible.length);$('queueEmpty').hidden=visible.length>0;if(visible.length&&!visible.some(b=>b.dataset.ticket===selected))select(visible[0],false)};
  function select(b, scroll=true){if(selected!==b.dataset.ticket)$('messageInput').value='';selected=b.dataset.ticket;document.querySelectorAll('.ticket-row').forEach(x=>x.classList.toggle('selected',x===b));const f=fixture[selected];$('ticketRef').textContent=`IT-${selected}`;$('ticketTitle').textContent=f[2];$('requesterName').textContent=f[0];$('requesterEmail').textContent=f[1];$('requesterAvatar').textContent=f[0].split(' ').map(s=>s[0]).join('');$('ticketStatus').textContent=b.dataset.status;$('propStatus').value=b.dataset.status;$('propPriority').value=b.dataset.priority;
   if(selected==='14')$('conversation').innerHTML=original;else{$('conversation').innerHTML='<div class="date-divider">Illustrative ticket · Today</div><article class="message"><div class="message-head"><span class="avatar rose" id="otherAvatar"></span><strong id="otherAuthor"></strong><span class="badge">Requester</span><span class="message-time">10:15</span></div><div class="message-audience">Via it-desk@helpdesk.test</div><div class="message-body"><p id="otherBody"></p></div></article><div class="activity-line">Illustrative conversation preview — no further messages shown</div>'; $('otherAvatar').textContent=f[0].split(' ').map(s=>s[0]).join('');$('otherAuthor').textContent=f[0];$('otherBody').textContent=f[3]}
   $('sendFrom').textContent=note?'Visible only to authorised IT Support agents':`From it-desk@helpdesk.test · To ${f[0]}`;$('messageInput').placeholder=`Write a reply to ${f[0].split(' ')[0]}…`;if(scroll)$('conversation').scrollTop=0;
  }
  document.querySelectorAll('[data-queue]').forEach(b=>b.addEventListener('click',()=>{queue=b.dataset.queue;document.querySelectorAll('[data-queue]').forEach(x=>x.classList.toggle('active',x===b));$('queueTitle').textContent=({mine:'My queue',team:'Team queue',unassigned:'Unassigned',all:'All tickets',priority:'At risk'})[queue];status()}));
  $('ticketFilter').addEventListener('input',status);
  document.querySelectorAll('.ticket-row').forEach(b=>b.addEventListener('click',()=>select(b)));
  let note=false;const setMode=n=>{note=n;const cmp=$('composer');cmp.classList.toggle('note-mode',n);$('replyMode').classList.toggle('active',!n);$('noteMode').classList.toggle('active',n);$('replyMode').setAttribute('aria-pressed',String(!n));$('noteMode').setAttribute('aria-pressed',String(n));$('composeModeLabel').textContent=n?'PRIVATE · STAFF ONLY':'EMAIL TO REQUESTER';$('sendFrom').textContent=n?'Visible only to authorised IT Support agents':`From it-desk@helpdesk.test · To ${fixture[selected][0]}`;$('sendLabel').textContent=n?'Add note':'Send reply';$('messageInput').setAttribute('aria-label',n?'Draft internal note':'Draft public reply');$('messageInput').placeholder=n?'Write an internal note for your colleagues…':`Write a reply to ${fixture[selected][0].split(' ')[0]}…`;$('statusAfter').hidden=n;$('statusAfterLabel').hidden=n;$('demoSend').classList.toggle('note-send',n)};
  $('replyMode').addEventListener('click',()=>setMode(false));$('noteMode').addEventListener('click',()=>setMode(true));$('demoSend').addEventListener('click',()=>window.graphiteToast(note?'Prototype only — internal note not created':'Prototype only — no email sent'));
  $('propStatus').addEventListener('change',()=>{$('ticketStatus').textContent=$('propStatus').value;window.graphiteToast('Sample status changed locally — no ticket updated')});
  status();
 }
})();
