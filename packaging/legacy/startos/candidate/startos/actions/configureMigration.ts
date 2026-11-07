import { i18n } from '../i18n'
import { storeJson } from '../fileModels/store.json'
import { sdk } from '../sdk'

const { InputSpec, Value } = sdk

const inputSpec = InputSpec.of({
  autoMigrate: Value.toggle({
    name: i18n('Automatically migrate BookshelfNG data on startup'),
    description: i18n('When disabled, ChaptarrNG waits for you to run the documented manual migration.'),
    default: true,
  }),
})

export const configureMigration = sdk.Action.withInput(
  'configure-migration',
  {
    name: i18n('Configure BookshelfNG migration'),
    description: i18n('Choose whether the existing library is converted automatically on first startup.'),
    warning: null,
    allowedStatuses: 'any',
    group: null,
    visibility: 'enabled',
  },
  inputSpec,
  async () => ({
    autoMigrate: (await storeJson.read(s => s.autoMigrate).once()) ?? true,
  }),
  async ({ effects, input }) => storeJson.merge(effects, { autoMigrate: input.autoMigrate }),
)
