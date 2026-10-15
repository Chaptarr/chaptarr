import ModelBase from 'App/ModelBase';

export interface Field {
  order: number;
  name: string;
  label: string;
  value: boolean | number | string;
  type: string;
  advanced: boolean;
  privacy: string;
}

export interface ImportListMonitoringOptions {
  addMissingAuthors: boolean;
  resumePausedAuthors: boolean;
  audiobookMonitored?: boolean | null;
  ebookMonitored?: boolean | null;
  audiobookMonitor?: 'all' | 'missing' | 'existing' | 'none' | null;
  ebookMonitor?: 'all' | 'missing' | 'existing' | 'none' | null;
  audiobookMonitorNewItems?: 'all' | 'none' | 'new' | null;
  ebookMonitorNewItems?: 'all' | 'none' | 'new' | null;
}

interface ImportList extends ModelBase {
  enable: boolean;
  enableAutomaticAdd: boolean;
  qualityProfileId: number;
  metadataProfileId: number;
  rootFolderPath: string;
  monitoringOptions?: ImportListMonitoringOptions | null;
  name: string;
  fields: Field[];
  implementationName: string;
  implementation: string;
  configContract: string;
  infoLink: string;
  tags: number[];
}

export default ImportList;
