import { setupManifest } from '@start9labs/start-sdk'
import { long, short } from './i18n'

export const manifest = setupManifest({
  id: 'bookshelfng',
  title: 'BookshelfNG',
  license: 'GPL-3.0-only',
  packageRepo: 'https://github.com/snapetech-org/bookshelfng-startos',
  upstreamRepo: 'https://github.com/snapetech-org/chaptarrng',
  marketingUrl: 'https://github.com/snapetech-org/chaptarrng',
  donationUrl: 'https://ko-fi.com/snapetech',
  description: { short, long },
  volumes: ['main', 'books', 'downloads', 'startos'],
  images: {
    'bookshelfng': {
      source: { dockerTag: 'ghcr.io/snapetech-org/chaptarrng@sha256:__CHAPTARRNG_IMAGE_DIGEST__' },
      arch: ['x86_64', 'aarch64'],
    },
  },
})
