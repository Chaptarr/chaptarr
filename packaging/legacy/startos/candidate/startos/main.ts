import { i18n } from './i18n'
import { storeJson } from './fileModels/store.json'
import { sdk } from './sdk'
import { uiPort } from './utils'

export const main = sdk.setupMain(async ({ effects }) => {
  /**
   * ======================== Setup (optional) ========================
   *
   * In this section, we fetch any resources or run any desired preliminary commands.
   */
  console.info(i18n('Starting BookshelfNG!'))
  const autoMigrate = (await storeJson.read(s => s.autoMigrate).const(effects)) ?? true

  const appSubcontainer = sdk.SubContainer.of(
    effects,
    { imageId: 'bookshelfng' },
    sdk.Mounts.of().mountVolume({
      volumeId: 'main',
      subpath: null,
      mountpoint: '/config',
      readonly: false,
    })
      .mountVolume({ volumeId: 'books', subpath: null, mountpoint: '/books', readonly: false })
      .mountVolume({ volumeId: 'downloads', subpath: null, mountpoint: '/downloads', readonly: false }),
    'bookshelfng-sub',
  )

  /**
   * ======================== Daemons ========================
   *
   * In this section, we create one or more daemons that define the service runtime.
   *
   * Each daemon defines its own health check, which can optionally be exposed to the user.
   */
  return sdk.Daemons.of(effects).addDaemon('primary', {
    subcontainer: appSubcontainer,
    exec: {
      command: sdk.useEntrypoint(),
      env: {
        CHAPTARR_BOOKSHELF_COMPATIBILITY: 'true',
        CHAPTARR_DEFAULT_PORT: '8787',
        CHAPTARR_EXPECTED_PORT: '8787',
        BOOKSHELFNG_MIGRATION_MODE: autoMigrate ? 'automatic' : 'manual',
        HARDCOVER: 'true',
        HARDCOVER_NATIVE: 'true',
      },
    },
    ready: {
      display: i18n('Web Interface'),
      // Keep readiness in Starting through the 245-minute bounded migration
      // window. Do not expose the package as ready until the migration marker
      // clears and the converted database/profile pass the health check.
      gracePeriod: 14_700_000,
      trigger: sdk.trigger.cooldownTrigger(10_000),
      fn: () =>
        sdk.healthCheck.checkPortListening(effects, uiPort, {
          successMessage: i18n('The web interface is ready'),
          errorMessage: i18n('The web interface is not ready'),
        }),
    },
    requires: [],
  })
})
