/* Local screenshot filtering only. */
'use strict';
try { document.documentElement.dataset.theme=localStorage.getItem('graphite-theme')||'light'; } catch {}
function filterGallery() {
 const theme=document.querySelector('#gallery-theme').value;
 const size=document.querySelector('#gallery-size').value;
 let count=0;
 document.querySelectorAll('.screen-preview').forEach(item=>{
  item.hidden=!(theme==='all'||item.dataset.themeFilter===theme)||!(size==='all'||item.dataset.sizeFilter===size);
  if(!item.hidden)count++;
 });
 document.querySelector('#gallery-count').textContent=count+' captures';
}
document.querySelectorAll('select').forEach(select=>select.addEventListener('change',filterGallery));
