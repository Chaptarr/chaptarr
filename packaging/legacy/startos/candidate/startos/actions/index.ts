import { sdk } from '../sdk'
import { configureMigration } from './configureMigration'

export const actions = sdk.Actions.of().addAction(configureMigration)
