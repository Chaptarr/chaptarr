import { IMPOSSIBLE, VersionInfo } from '@start9labs/start-sdk'

export const current = VersionInfo.of({
  version: '__CHAPTARRNG_STARTOS_VERSION__',
  releaseNotes: {
    en_US: 'Switch the BookshelfNG package runtime to ChaptarrNG. Choose automatic or manual first-start migration in the package action.',
    es_ES: 'Cambiar el runtime del paquete BookshelfNG a ChaptarrNG. Elige la migración inicial automática o manual en la acción del paquete.',
    de_DE: 'Laufzeit des BookshelfNG-Pakets auf ChaptarrNG umstellen. Wähle die automatische oder manuelle Erststart-Migration in der Paketaktion.',
    pl_PL: 'Zmiana środowiska pakietu BookshelfNG na ChaptarrNG. Wybierz automatyczną lub ręczną migrację przy pierwszym uruchomieniu w działaniu pakietu.',
    fr_FR: 'Passage du runtime du paquet BookshelfNG à ChaptarrNG. Choisissez la migration automatique ou manuelle au premier démarrage dans l’action du paquet.',
  },
  migrations: {
    up: async ({ effects }) => {},
    down: IMPOSSIBLE,
  },
})
