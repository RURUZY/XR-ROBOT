import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.join(path.dirname(fileURLToPath(import.meta.url)),'dist');
const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8','.json':'application/json; charset=utf-8'};
http.createServer(async(req,res)=>{try{const url=new URL(req.url,'http://localhost');const decoded=decodeURIComponent(url.pathname);const target=path.resolve(root,'.'+(decoded==='/'?'/index.html':decoded));if(!target.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}const body=await fs.readFile(target);res.writeHead(200,{'Content-Type':types[path.extname(target)]||'application/octet-stream','Cache-Control':'no-store'});res.end(body);}catch{res.writeHead(404);res.end('Not found');}}).listen(8766,'127.0.0.1',()=>console.log('KAT Angle Lab: http://127.0.0.1:8766'));
