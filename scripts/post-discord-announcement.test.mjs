import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

const script = `
  const guild = process.env.TEST_GUILD_ID;
  let posts = 0;
  globalThis.fetch = async (_url, options = {}) => {
    if (options.method === 'POST') {
      posts += 1;
      return { ok: true, json: async () => ({
        id: '987654321', guild_id: guild, channel_id: '123456789',
        embeds: [{ description: process.env.ANNOUNCEMENT_BODY }],
      }) };
    }
    return { ok: true, json: async () => ({ guild_id: guild, channel_id: '123456789' }) };
  };
  try { await import('./post-discord-announcement.mjs'); }
  finally { console.log('POST_COUNT=' + posts); }
`;

function run(guildId) {
  return spawnSync(process.execPath, ['--input-type=module', '-e', script], {
    cwd: import.meta.dirname,
    encoding: 'utf8',
    env: {
      ...process.env,
      DISCORD_RELEASE_WEBHOOK: 'https://discord.com/api/webhooks/123456789/example-token',
      ANNOUNCEMENT_TITLE: 'Test release',
      ANNOUNCEMENT_URL: 'https://example.org/release',
      ANNOUNCEMENT_BODY: 'Test announcement',
      TEST_GUILD_ID: guildId,
    },
  });
}

test('wrong Discord server is rejected before sending', () => {
  const result = run('1447333772419928256');
  assert.ifError(result.error);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /outside the Chaptarr community server/);
  assert.match(result.stdout, /POST_COUNT=0/);
});

test('right Discord server sends and records a message link', () => {
  const result = run('1376676460647022752');
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /discord.com\/channels\/1376676460647022752\/123456789\/987654321/);
  assert.match(result.stdout, /POST_COUNT=1/);
});
