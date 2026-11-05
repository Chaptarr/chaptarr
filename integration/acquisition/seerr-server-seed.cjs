// Synthetic accounts and service settings for the isolated full-server test.
const fs = require('node:fs');
const fixture = JSON.parse(fs.readFileSync('/fixture/manifest.json'));
process.env.NODE_ENV = 'production';
process.env.CONFIG_DIRECTORY = '/fixture/seerr-config';
require('/app/node_modules/reflect-metadata');
(async () => {
  const dataSource = require('/app/dist/datasource.js').default;
  await dataSource.initialize();
  await require('/app/dist/lib/startupMigrations.js').runStartupMigrations(dataSource);
  const { getSettings } = require('/app/dist/lib/settings/index.js');
  const settings = await getSettings().load();
  settings.main = { ...settings.main, apiKey: 'synthetic-seerr-web-admin-key-20261105', localLogin: true, versionCheck: false };
  settings.public = { ...settings.public, initialized: true };
  settings.readarr = [{ id: 1, name: 'Isolated migrated ChaptarrNG', hostname: '127.0.0.1', port: 8789,
    apiKey: 'synthetic-e2e-restricted-key-20261105', useSsl: false, baseUrl: '',
    activeProfileId: fixture.qualityProfileId, activeProfileName: 'Fixture',
    activeMetadataProfileId: fixture.metadataProfileId, activeDirectory: '/library',
    tags: [], is4k: false, isDefault: true, syncEnabled: true, preventSearch: false,
    tagRequests: false, overrideRule: [], serviceType: fixture.mediaType }];
  await settings.save();

  const { User } = require('/app/dist/entity/User.js');
  const { Permission } = require('/app/dist/lib/permissions.js');
  require('node:assert/strict').equal(await dataSource.getRepository(User).count(), 0, 'Refuse to seed existing accounts');
  for (const [email, permissions] of [
    ['fixture-owner@example.invalid', Permission.ADMIN],
    ['fixture-requester@example.invalid', Permission.REQUEST + Permission.REQUEST_BOOK + Permission.REQUEST_VIEW],
  ]) {
    const user = new User();
    user.email = email; user.permissions = permissions; user.userType = 2; user.avatar = '';
    await user.setPassword('Synthetic-Fixture-Password-20261105');
    await dataSource.getRepository(User).save(user);
  }
  await dataSource.destroy();
  console.log('Synthetic Seerr server accounts, database and service settings initialized.');
})().catch(error => { console.error(error); process.exitCode = 1; });
