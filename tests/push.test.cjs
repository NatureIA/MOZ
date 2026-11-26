'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const handlers={},notices=[],messages=[],opened=[];
let windows=[];
const self={location:{origin:'https://moz.runasp.net'},registration:{showNotification:async(title,opts)=>{notices.push({title,opts})}},addEventListener:(k,cb)=>{handlers[k]=cb}};
const clients={matchAll:async()=>windows,openWindow:async(url)=>{opened.push(url)}};
vm.runInNewContext(fs.readFileSync('wwwroot/service-worker.js','utf8'),{self,clients,URL});
(async()=>{
 let pending;
 handlers.push({data:{json:()=>({title:'MOZ',body:'Novo evento',tag:'moz-event-a'})},waitUntil:p=>pending=p});
 await pending;
 assert.equal(notices.length,1);assert.equal(notices[0].opts.tag,'moz-event-a');assert.equal(notices[0].opts.data.url,'/?notifications=1');
 handlers.notificationclick({notification:{close(){}},waitUntil:p=>pending=p});await pending;assert.equal(opened[0],'/?notifications=1');
 windows=[{url:'https://moz.runasp.net/',focus:async()=>{},postMessage:m=>messages.push(m)}];
 handlers.notificationclick({notification:{close(){}},waitUntil:p=>pending=p});await pending;assert.equal(messages[0].type,'MOZ_OPEN_NOTICES');
 console.log('Web Push service worker: background notification and navigation.');
})().catch(e=>{console.error(e);process.exitCode=1});
