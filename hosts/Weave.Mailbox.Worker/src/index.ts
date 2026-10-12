import { demo } from './demo';
import { directory, mutateAddress, ownedAddresses } from './addresses';
import { sweep } from './cleanup';
import { budgetDatabase } from './budget';
import { stream, type Context } from './stream';
import { authenticate, register } from './accounts';
import { putCard } from './cards';
import { decide, requestContact, setBlocked } from './contacts';
import { atomic, type Env } from './database';
import { ack, send } from './messages';
import { read } from './reads';
import { body, control, fail, json, problem } from './wire';

export default {
    async fetch(request: Request, env: Env, context: Context): Promise<Response> {
        env = {...env,DB:budgetDatabase(env.DB)};
        try {
            const url = new URL(request.url), secret = control(request), method = request.method;
            if (url.protocol !== 'https:' && !['localhost','127.0.0.1','[::1]'].includes(url.hostname)) fail(400,'invalid');
            if (method === 'GET' && url.pathname === '/') return demo();
            if (method === 'POST' && url.pathname === '/v1/accounts') return json(await register(env,request,secret,await body(request)));
            const account = await authenticate(env,secret);
            if (method === 'GET' && ['/v1/directory','/v1/directory/address'].includes(url.pathname)) return json(await atomic(env.DB,tx => directory(tx,url)));
            if (method === 'GET' && ['/v1/contacts/cards','/v1/contacts/card'].includes(url.pathname)) return json(await atomic(env.DB,tx => read(tx,url,account?.accountId ?? null)));
            if (!account) fail(401,'unauthenticated');
            if (url.pathname === '/v1/addresses' || /^\/v1\/addresses\/[^/]+$/.test(url.pathname)) {
                const id = url.pathname === '/v1/addresses' ? null : url.pathname.split('/')[3];
                if (method === 'GET') return json(await atomic(env.DB,tx => ownedAddresses(tx,account.accountId,url)));
                if (method === 'POST' && id === null || method === 'PUT' && id !== null) return json(await mutateAddress(env,account.accountId,id,await body(request)));
                if (method === 'DELETE' && id !== null) return json(await mutateAddress(env,account.accountId,id,null,true));
                fail(404,'unavailable');
            }
            const match = url.pathname.match(/^\/(inbox|outbox)\/([^/]+)$/);
            if (match) {
                if (decodeURIComponent(match[2]) !== account.accountId) fail(404,'unavailable');
                url.pathname = match[1] === 'outbox' && method === 'POST' ? '/v1/messages' : '/v1/'+match[1];
            }
            const owner = account.accountId;
            if (method === 'GET' && url.pathname === '/v1/inbox/events') return await stream(env,owner,request,context);
            if (method === 'GET') return json(await atomic(env.DB,tx => read(tx,url,owner)));
            const input = await body(request);
            await sweep(env);
            return json(await atomic(env.DB,async tx => {
                if (method === 'PUT' && url.pathname === '/v1/contacts/cards') return putCard(tx,owner,input);
                if (method === 'POST' && url.pathname === '/v1/contacts/requests') return requestContact(tx,owner,input);
                const decision = url.pathname.match(/^\/v1\/contacts\/requests\/([^/]+)\/decision$/);
                if (method === 'POST' && decision) return decide(tx,owner,decision[1],input);
                if (['PUT','DELETE'].includes(method) && url.pathname === '/v1/contacts/blocks') return setBlocked(tx,owner,input,method === 'PUT');
                if (method === 'POST' && url.pathname === '/v1/messages') return send(tx,owner,input);
                const acknowledge = url.pathname.match(/^\/v1\/inbox\/([^/]+)\/ack$/);
                if (method === 'POST' && acknowledge) return ack(tx,owner,acknowledge[1],input);
                fail(404,'unavailable');
            }));
        } catch (error) { return problem(error); }
    }
};
