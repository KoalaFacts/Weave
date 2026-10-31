import assert from 'node:assert/strict';
import test from 'node:test';
import { planLatestSchema } from '../scripts/latest-schema.ts';
import { start } from './harness.ts';

/**
 * The pre-human accounts table as defined by main before onboarding #306.
 * Preserve the old NOT NULL control credentials and absence of human_id:
 * a fresh current-schema fixture cannot exercise this release boundary.
 */
const preHumanAccounts = `CREATE TABLE "accounts" (
    "account_id" text PRIMARY KEY NOT NULL,
    "control_hash" text NOT NULL,
    "control_hash_scheme" text NOT NULL,
    "control_hash_parameters" text NOT NULL,
    "display_name" text,
    "description" text,
    "created_at" integer,
    "updated_at" integer,
    CONSTRAINT "accounts_control_digest" CHECK(length(control_hash)=64 AND control_hash NOT GLOB '*[^0-9a-f]*'),
    CONSTRAINT "accounts_control_scheme" CHECK(control_hash_scheme='sha256-utf8-v1'),
    CONSTRAINT "accounts_control_parameters" CHECK(json_valid(control_hash_parameters))
)`;

/**
 * Read-only schema planning must reject the actual pre-human column layout
 * without rewriting an existing account. This test does NOT approve or
 * exercise any live D1 migration, reset or Cloudflare operation.
 */
test('onboarding schema preflight rejects a populated pre-human accounts table without data loss', async () => {
    const h = await start({ migrate: false });
    try {
        await h.db.prepare(preHumanAccounts).run();
        await h.db.prepare('CREATE UNIQUE INDEX "accounts_control_hash" ON "accounts" ("control_hash")').run();
        const accountId = '11111111-2222-4333-8444-555555555555';
        const digest = 'a'.repeat(64);
        await h.db.prepare(
            'INSERT INTO accounts (account_id, control_hash, control_hash_scheme, control_hash_parameters, display_name, created_at, updated_at) VALUES (?,?,?,?,?,?,?)'
        ).bind(accountId, digest, 'sha256-utf8-v1', '{}', 'Existing agent', '123456789', '123456789').run();
        const before = (await h.db.prepare('SELECT * FROM accounts WHERE account_id=?').bind(accountId).all()).results;
        const originalDefinition = await h.db.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name='accounts'").first('sql');

        await assert.rejects(planLatestSchema(h.db), /Existing columns differ from the latest schema in accounts/);

        assert.deepEqual((await h.db.prepare('SELECT * FROM accounts WHERE account_id=?').bind(accountId).all()).results, before);
        assert.equal(await h.db.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name='accounts'").first('sql'), originalDefinition);
        assert.equal(await h.db.prepare("SELECT count(*) AS n FROM pragma_table_info('accounts') WHERE name='human_id'").first('n'), 0);
        assert.equal(await h.db.prepare("SELECT count(*) AS n FROM sqlite_master WHERE type='table' AND name='humans'").first('n'), 0);
    } finally {
        await h.close();
    }
});
