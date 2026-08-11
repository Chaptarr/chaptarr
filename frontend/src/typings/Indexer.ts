import ModelBase from 'App/ModelBase';

export interface Field {
  order: number;
  name: string;
  label: string;
  value: boolean | number | string | null;
  type: string;
  advanced: boolean;
  privacy: string;
}

export interface ProviderMessage {
  message: string | null;
  type: 'info' | 'warning' | 'error';
}

export type IndexerProtocol = 'unknown' | 'usenet' | 'torrent' | 'direct';

interface Indexer extends ModelBase {
  enableRss: boolean;
  enableAutomaticSearch: boolean;
  enableInteractiveSearch: boolean;
  enable: boolean;
  supportsRss: boolean;
  supportsSearch: boolean;
  protocol: IndexerProtocol;
  priority: number;
  downloadClientId: number;
  proxyId: number | null;
  name: string;
  fields: Field[];
  implementationName: string;
  implementation: string;
  configContract: string;
  infoLink: string;
  message: ProviderMessage | null;
  tags: number[];
  presets: Indexer[] | null;
}

export default Indexer;
